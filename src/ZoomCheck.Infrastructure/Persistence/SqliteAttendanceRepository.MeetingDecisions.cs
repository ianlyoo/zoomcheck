using Microsoft.Data.Sqlite;
using ZoomCheck.Core.Models;

namespace ZoomCheck.Infrastructure.Persistence;

public sealed partial class SqliteAttendanceRepository
{
    private static async Task InitializeMeetingDecisionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS meeting_exclusions (
                meeting_id TEXT NOT NULL, attendance_date TEXT NOT NULL, roster_person_id TEXT NOT NULL, updated_at TEXT NOT NULL,
                PRIMARY KEY (meeting_id, attendance_date, roster_person_id)
            );
            CREATE TABLE IF NOT EXISTS meeting_reviews (
                meeting_id TEXT NOT NULL, subject_key TEXT NOT NULL, kind TEXT NOT NULL,
                status TEXT NOT NULL, evidence_token TEXT NOT NULL, updated_at TEXT NOT NULL,
                PRIMARY KEY (meeting_id, subject_key, kind)
            );
            CREATE TABLE IF NOT EXISTS meeting_connection_observations (
                meeting_id TEXT NOT NULL, source TEXT NOT NULL, presence_key TEXT NOT NULL,
                roster_person_id TEXT NOT NULL, observed_name TEXT NOT NULL,
                observed_email TEXT NOT NULL, first_seen_at TEXT NOT NULL, observed_at TEXT NOT NULL,
                PRIMARY KEY (meeting_id, source, presence_key, first_seen_at, observed_name, observed_email)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        // Preserve observations created by earlier development builds, once only.
        if (await TableExistsAsync(connection, "meeting_connection_matches", cancellationToken))
        {
            await using var migrate = connection.CreateCommand();
            migrate.CommandText = """
                INSERT OR IGNORE INTO meeting_connection_observations
                    (meeting_id, source, presence_key, roster_person_id, observed_name, observed_email, first_seen_at, observed_at)
                SELECT m.meeting_id, m.source, m.presence_key, m.roster_person_id, m.observed_name, m.observed_email, m.first_seen_at,
                    COALESCE((SELECT p.last_seen_at FROM participant_presence_v2 p
                        WHERE p.meeting_id=m.meeting_id AND p.source=m.source AND p.presence_key=m.presence_key), m.first_seen_at)
                FROM meeting_connection_matches m
                WHERE NOT EXISTS (SELECT 1 FROM schema_migrations WHERE migration_id='meeting-connection-observations');
                INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at)
                    VALUES ('meeting-connection-observations', $now);
                """;
            migrate.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlySet<string>> GetMeetingExclusionsAsync(string meetingId, string attendanceDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT roster_person_id FROM meeting_exclusions WHERE meeting_id = $meetingId AND attendance_date = $date;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$date", attendanceDate);
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { result.Add(reader.GetString(0)); }
        return result;
    }

    public async Task SetMeetingExclusionAsync(string meetingId, string attendanceDate, string personId, bool excluded, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = excluded
            ? "INSERT INTO meeting_exclusions (meeting_id, attendance_date, roster_person_id, updated_at) VALUES ($meetingId, $date, $personId, $now) ON CONFLICT(meeting_id, attendance_date, roster_person_id) DO UPDATE SET updated_at = excluded.updated_at;"
            : "DELETE FROM meeting_exclusions WHERE meeting_id = $meetingId AND attendance_date = $date AND roster_person_id = $personId;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$date", attendanceDate);
        command.Parameters.AddWithValue("$personId", personId);
        if (excluded) { command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MeetingReviewDecision>> GetMeetingReviewsAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT subject_key, kind, status, evidence_token, updated_at FROM meeting_reviews WHERE meeting_id = $meetingId;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        var result = new List<MeetingReviewDecision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        }
        return result;
    }

    public async Task SaveMeetingReviewAsync(string meetingId, MeetingReviewDecision decision, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO meeting_reviews (meeting_id, subject_key, kind, status, evidence_token, updated_at)
            VALUES ($meetingId, $subject, $kind, $status, $evidence, $now)
            ON CONFLICT(meeting_id, subject_key, kind) DO UPDATE SET
                status = excluded.status, evidence_token = excluded.evidence_token, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$subject", decision.SubjectKey);
        command.Parameters.AddWithValue("$kind", decision.Kind);
        command.Parameters.AddWithValue("$status", decision.Status);
        command.Parameters.AddWithValue("$evidence", decision.EvidenceToken);
        command.Parameters.AddWithValue("$now", decision.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MeetingConnectionMatch>> GetMeetingConnectionMatchesAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source, presence_key, roster_person_id, observed_name, observed_email, first_seen_at, observed_at FROM meeting_connection_observations WHERE meeting_id = $meetingId;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        var result = new List<MeetingConnectionMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6))));
        }
        return result;
    }

    public async Task SaveMeetingConnectionMatchAsync(string meetingId, MeetingConnectionMatch match, bool remove, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = remove
            ? "DELETE FROM meeting_connection_observations WHERE meeting_id = $meetingId AND source = $source AND presence_key = $key AND first_seen_at = $firstSeen AND observed_name = $name AND observed_email = $email;"
            : """
                INSERT INTO meeting_connection_observations (meeting_id, source, presence_key, roster_person_id, observed_name, observed_email, first_seen_at, observed_at)
                VALUES ($meetingId, $source, $key, $personId, $name, $email, $firstSeen, $observedAt)
                ON CONFLICT(meeting_id, source, presence_key, first_seen_at, observed_name, observed_email) DO UPDATE SET
                    roster_person_id = excluded.roster_person_id;
                """;
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$source", match.Source);
        command.Parameters.AddWithValue("$key", match.PresenceKey);
        command.Parameters.AddWithValue("$name", match.ObservedName);
        command.Parameters.AddWithValue("$email", match.ObservedEmail);
        command.Parameters.AddWithValue("$firstSeen", match.FirstSeenAt.ToString("O"));
        if (!remove)
        {
            command.Parameters.AddWithValue("$personId", match.RosterPersonId);
            command.Parameters.AddWithValue("$observedAt", match.ObservedAt.ToString("O"));
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
