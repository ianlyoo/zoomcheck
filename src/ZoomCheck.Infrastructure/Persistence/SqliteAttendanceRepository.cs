using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;

namespace ZoomCheck.Infrastructure.Persistence;

public sealed class SqliteAttendanceRepository
{
    private readonly string _connectionString;

    public SqliteAttendanceRepository(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var commands = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS roster_imports (
                import_id TEXT PRIMARY KEY,
                source_path TEXT NOT NULL,
                display_name TEXT NOT NULL,
                imported_at TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS roster_people (
                id TEXT PRIMARY KEY,
                import_id TEXT NOT NULL,
                sequence TEXT NOT NULL,
                name TEXT NOT NULL,
                normalized_name TEXT NOT NULL,
                email TEXT NOT NULL,
                phone TEXT NOT NULL,
                organization TEXT NOT NULL,
                aliases_json TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS manual_aliases (
                alias_key TEXT PRIMARY KEY,
                roster_person_id TEXT NOT NULL,
                note TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS participant_events (
                id TEXT PRIMARY KEY,
                meeting_id TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                event_type TEXT NOT NULL,
                participant_name TEXT NOT NULL,
                normalized_participant_name TEXT NOT NULL,
                participant_email TEXT NULL,
                confidence TEXT NOT NULL,
                matched_roster_person_id TEXT NULL,
                source TEXT NOT NULL,
                raw_payload TEXT NOT NULL
            );
            """
            ,
            """
            CREATE TABLE IF NOT EXISTS participant_presence (
                meeting_id TEXT NOT NULL,
                source TEXT NOT NULL,
                normalized_name TEXT NOT NULL,
                display_name TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY (meeting_id, source, normalized_name)
            );
            """
        };

        foreach (var sql in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task ReplaceRosterAsync(RosterImportResult roster, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var text in new[] { "DELETE FROM roster_people;", "DELETE FROM roster_imports;" })
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = text;
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertImport = connection.CreateCommand())
        {
            insertImport.Transaction = transaction;
            insertImport.CommandText = "INSERT INTO roster_imports (import_id, source_path, display_name, imported_at) VALUES ($id, $path, $name, $importedAt);";
            insertImport.Parameters.AddWithValue("$id", roster.ImportId);
            insertImport.Parameters.AddWithValue("$path", roster.SourcePath);
            insertImport.Parameters.AddWithValue("$name", roster.DisplayName);
            insertImport.Parameters.AddWithValue("$importedAt", roster.ImportedAt.ToString("O"));
            await insertImport.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var person in roster.People)
        {
            await using var insertPerson = connection.CreateCommand();
            insertPerson.Transaction = transaction;
            insertPerson.CommandText =
                "INSERT INTO roster_people (id, import_id, sequence, name, normalized_name, email, phone, organization, aliases_json) VALUES ($id, $importId, $sequence, $name, $normalizedName, $email, $phone, $organization, $aliases);";
            insertPerson.Parameters.AddWithValue("$id", person.Id);
            insertPerson.Parameters.AddWithValue("$importId", roster.ImportId);
            insertPerson.Parameters.AddWithValue("$sequence", person.Sequence);
            insertPerson.Parameters.AddWithValue("$name", person.Name);
            insertPerson.Parameters.AddWithValue("$normalizedName", person.NormalizedName);
            insertPerson.Parameters.AddWithValue("$email", person.Email);
            insertPerson.Parameters.AddWithValue("$phone", person.Phone);
            insertPerson.Parameters.AddWithValue("$organization", person.Organization);
            insertPerson.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(person.Aliases));
            await insertPerson.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RosterPerson>> GetRosterPeopleAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, sequence, name, normalized_name, email, phone, organization, aliases_json FROM roster_people ORDER BY CAST(sequence AS INTEGER);";

        var people = new List<RosterPerson>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var aliases = JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? Array.Empty<string>();
            people.Add(new RosterPerson(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                aliases));
        }

        return people;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAliasMapAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT alias_key, roster_person_id FROM manual_aliases;";

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            aliases[reader.GetString(0)] = reader.GetString(1);
        }

        return aliases;
    }

    public async Task UpsertAliasAsync(string aliasKey, string rosterPersonId, string note, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO manual_aliases (alias_key, roster_person_id, note, created_at) VALUES ($aliasKey, $rosterPersonId, $note, $createdAt) ON CONFLICT(alias_key) DO UPDATE SET roster_person_id = excluded.roster_person_id, note = excluded.note, created_at = excluded.created_at;";
        command.Parameters.AddWithValue("$aliasKey", aliasKey);
        command.Parameters.AddWithValue("$rosterPersonId", rosterPersonId);
        command.Parameters.AddWithValue("$note", note);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendParticipantEventAsync(ParticipantEvent participantEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        BindParticipantEvent(command, participantEvent);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the participants a capture source last reported as present for a meeting.
    /// Presence is scoped by (meeting, source) so snapshots from one source never imply
    /// that participants observed by another source have left.
    /// </summary>
    public async Task<IReadOnlyList<ParticipantSnapshotEntry>> GetParticipantPresenceAsync(string meetingId, string source, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT normalized_name, display_name, first_seen_at, last_seen_at FROM participant_presence WHERE meeting_id = $meetingId AND source = $source;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$source", source);

        var entries = new List<ParticipantSnapshotEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new ParticipantSnapshotEntry(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return entries;
    }

    /// <summary>
    /// Atomically appends the derived join/leave events and replaces the presence set
    /// for the given (meeting, source) pair.
    /// </summary>
    public async Task ApplyParticipantSnapshotAsync(
        string meetingId,
        string source,
        IReadOnlyList<ParticipantSnapshotEntry> presentEntries,
        IReadOnlyList<ParticipantEvent> derivedEvents,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var derivedEvent in derivedEvents)
        {
            await using var insertEvent = connection.CreateCommand();
            insertEvent.Transaction = transaction;
            BindParticipantEvent(insertEvent, derivedEvent);
            await insertEvent.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clearPresence = connection.CreateCommand())
        {
            clearPresence.Transaction = transaction;
            clearPresence.CommandText = "DELETE FROM participant_presence WHERE meeting_id = $meetingId AND source = $source;";
            clearPresence.Parameters.AddWithValue("$meetingId", meetingId);
            clearPresence.Parameters.AddWithValue("$source", source);
            await clearPresence.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var entry in presentEntries)
        {
            await using var insertPresence = connection.CreateCommand();
            insertPresence.Transaction = transaction;
            insertPresence.CommandText =
                "INSERT INTO participant_presence (meeting_id, source, normalized_name, display_name, first_seen_at, last_seen_at) VALUES ($meetingId, $source, $normalizedName, $displayName, $firstSeenAt, $lastSeenAt);";
            insertPresence.Parameters.AddWithValue("$meetingId", meetingId);
            insertPresence.Parameters.AddWithValue("$source", source);
            insertPresence.Parameters.AddWithValue("$normalizedName", entry.NormalizedName);
            insertPresence.Parameters.AddWithValue("$displayName", entry.DisplayName);
            insertPresence.Parameters.AddWithValue("$firstSeenAt", entry.FirstSeenAt.ToString("O"));
            insertPresence.Parameters.AddWithValue("$lastSeenAt", entry.LastSeenAt.ToString("O"));
            await insertPresence.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantEvent>> GetParticipantEventsAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload FROM participant_events WHERE meeting_id = $meetingId ORDER BY occurred_at ASC;";
        command.Parameters.AddWithValue("$meetingId", meetingId);

        var events = new List<ParticipantEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new ParticipantEvent(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                Enum.Parse<ParticipantEventType>(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Enum.Parse<MatchConfidence>(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10)));
        }

        return events;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void BindParticipantEvent(SqliteCommand command, ParticipantEvent participantEvent)
    {
        command.CommandText =
            "INSERT INTO participant_events (id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload) VALUES ($id, $meetingId, $occurredAt, $eventType, $participantName, $normalizedParticipantName, $participantEmail, $confidence, $matchedRosterPersonId, $source, $rawPayload);";
        command.Parameters.AddWithValue("$id", participantEvent.Id);
        command.Parameters.AddWithValue("$meetingId", participantEvent.MeetingId);
        command.Parameters.AddWithValue("$occurredAt", participantEvent.OccurredAt.ToString("O"));
        command.Parameters.AddWithValue("$eventType", participantEvent.EventType.ToString());
        command.Parameters.AddWithValue("$participantName", participantEvent.ParticipantName);
        command.Parameters.AddWithValue("$normalizedParticipantName", participantEvent.NormalizedParticipantName);
        command.Parameters.AddWithValue("$participantEmail", (object?)participantEvent.ParticipantEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("$confidence", participantEvent.Confidence.ToString());
        command.Parameters.AddWithValue("$matchedRosterPersonId", (object?)participantEvent.MatchedRosterPersonId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", participantEvent.Source);
        command.Parameters.AddWithValue("$rawPayload", participantEvent.RawPayload);
    }
}
