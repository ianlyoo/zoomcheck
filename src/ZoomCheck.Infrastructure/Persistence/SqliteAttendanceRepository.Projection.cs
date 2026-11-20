using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;

namespace ZoomCheck.Infrastructure.Persistence;

internal sealed record PersonEventSummary(
    string RosterPersonId,
    int EventCount,
    int JoinCount,
    DateTimeOffset? LastJoinedAt,
    DateTimeOffset? LastLeftAt,
    ParticipantEventType? LastNonRenameEventType,
    MatchConfidence MaxConfidence,
    string ConfidenceOrder);

internal sealed record NameEventSummary(
    string NormalizedParticipantName,
    int TotalEventCount,
    int UnmatchedEventCount,
    string? UnmatchedLatestName,
    ParticipantEventType? UnmatchedLatestEventType,
    DateTimeOffset? UnmatchedLatestOccurredAt);

internal sealed record SnapshotReadContext(
    IReadOnlyList<RosterPerson> Roster,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<ParticipantSnapshotEntry> PreviousPresence);

internal sealed record SnapshotDelta(
    IReadOnlyList<string> RemovedPresenceKeys,
    IReadOnlyList<ParticipantSnapshotEntry> AddedEntries,
    IReadOnlyList<ParticipantSnapshotEntry> UpdatedEntries);

internal sealed record BoardProjectionData(
    IReadOnlyList<RosterPerson> Roster,
    IReadOnlyDictionary<string, string> Aliases,
    IReadOnlyList<ParticipantSnapshotSourceState> SnapshotSources,
    IReadOnlyList<(string Source, ParticipantSnapshotEntry Entry)> CurrentPresence,
    IReadOnlySet<string> Exclusions,
    IReadOnlyList<MeetingReviewDecision> Reviews,
    IReadOnlyList<MeetingConnectionMatch> MeetingMatches,
    IReadOnlyDictionary<string, PersonEventSummary> PersonSummaries,
    IReadOnlyDictionary<string, NameEventSummary> NameSummaries,
    IReadOnlyList<ParticipantEvent> RecentEvents,
    IReadOnlyList<ParticipantEvent> MatchedConnectionEvents,
    IReadOnlyList<ParticipantEvent> AffectedPersonEvents,
    IReadOnlyList<ParticipantEvent> AffectedNameEvents);

public sealed partial class SqliteAttendanceRepository
{
    private const string ZoomLiveSnapshotSource = "zoom-live-participants";
    private const int RecentEventsLimit = 30;

    private static async Task InitializeEventProjectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await EnsureColumnExistsAsync(
            connection,
            tableName: "participant_events",
            columnName: "occurred_at_ticks",
            definition: "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);

        await using (var createObjects = connection.CreateCommand())
        {
            createObjects.CommandText = """
                CREATE TABLE IF NOT EXISTS meeting_person_event_summary (
                    meeting_id TEXT NOT NULL,
                    roster_person_id TEXT NOT NULL,
                    event_count INTEGER NOT NULL,
                    join_count INTEGER NOT NULL,
                    last_joined_at TEXT NULL,
                    last_joined_ticks INTEGER NULL,
                    last_left_at TEXT NULL,
                    last_left_ticks INTEGER NULL,
                    last_non_rename_event_type TEXT NULL,
                    last_non_rename_ticks INTEGER NULL,
                    last_non_rename_rowid INTEGER NOT NULL DEFAULT 0,
                    max_confidence INTEGER NOT NULL,
                    confidence_order TEXT NOT NULL,
                    PRIMARY KEY (meeting_id, roster_person_id)
                );
                CREATE TABLE IF NOT EXISTS meeting_name_event_summary (
                    meeting_id TEXT NOT NULL,
                    normalized_participant_name TEXT NOT NULL,
                    total_event_count INTEGER NOT NULL,
                    unmatched_event_count INTEGER NOT NULL,
                    unmatched_latest_name TEXT NULL,
                    unmatched_latest_event_type TEXT NULL,
                    unmatched_latest_occurred_at TEXT NULL,
                    unmatched_latest_ticks INTEGER NULL,
                    unmatched_latest_rowid INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (meeting_id, normalized_participant_name)
                );
                CREATE INDEX IF NOT EXISTS ix_participant_events_meeting_ticks
                    ON participant_events (meeting_id, occurred_at_ticks DESC);
                """;
            await createObjects.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var alreadyApplied = connection.CreateCommand())
        {
            alreadyApplied.CommandText =
                "SELECT 1 FROM schema_migrations WHERE migration_id = 'event-projection-v1' LIMIT 1;";
            if (await alreadyApplied.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return;
            }
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existingRows = new List<(long RowId, ParticipantEvent Event)>();
        await using (var readExisting = connection.CreateCommand())
        {
            readExisting.Transaction = transaction;
            readExisting.CommandText =
                "SELECT rowid, id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name FROM participant_events ORDER BY rowid ASC;";
            await using var reader = await readExisting.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingRows.Add((reader.GetInt64(0), ReadParticipantEvent(reader, offset: 1)));
            }
        }

        if (existingRows.Count > 0)
        {
            await using var updateTicks = connection.CreateCommand();
            updateTicks.Transaction = transaction;
            updateTicks.CommandText = "UPDATE participant_events SET occurred_at_ticks = $ticks WHERE rowid = $rowId;";
            var ticksParam = updateTicks.Parameters.Add("$ticks", SqliteType.Integer);
            var rowIdParam = updateTicks.Parameters.Add("$rowId", SqliteType.Integer);
            updateTicks.Prepare();

            foreach (var (rowId, evt) in existingRows)
            {
                ticksParam.Value = evt.OccurredAt.UtcTicks;
                rowIdParam.Value = rowId;
                await updateTicks.ExecuteNonQueryAsync(cancellationToken);
            }

            existingRows.Sort((left, right) =>
            {
                var timeComparison = left.Event.OccurredAt.CompareTo(right.Event.OccurredAt);
                return timeComparison != 0 ? timeComparison : left.RowId.CompareTo(right.RowId);
            });

            using var summaryWriter = new EventSummaryUpsertCommands(connection, transaction);
            foreach (var (rowId, evt) in existingRows)
            {
                await summaryWriter.ApplyEventAsync(evt, rowId, cancellationToken);
            }
        }

        await using (var markApplied = connection.CreateCommand())
        {
            markApplied.Transaction = transaction;
            markApplied.CommandText =
                "INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at) VALUES ('event-projection-v1', $appliedAt);";
            markApplied.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await markApplied.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<SnapshotReadContext> ReadSnapshotContextAsync(
        string meetingId,
        string source,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var roster = await ReadRosterPeopleOnConnectionAsync(connection, cancellationToken);
        var aliases = await ReadAliasMapOnConnectionAsync(connection, cancellationToken);
        var previousPresence = await ReadParticipantPresenceOnConnectionAsync(connection, meetingId, source, cancellationToken);
        return new SnapshotReadContext(roster, aliases, previousPresence);
    }

    internal async Task PersistSnapshotDeltaAsync(
        string meetingId,
        string source,
        DateTimeOffset capturedAt,
        int presentCount,
        bool hasPreviousPresence,
        SnapshotDelta delta,
        IReadOnlyList<ParticipantEvent> derivedEvents,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await PersistSnapshotDeltaOnConnectionAsync(
            connection,
            meetingId,
            source,
            capturedAt,
            presentCount,
            hasPreviousPresence,
            delta,
            derivedEvents,
            cancellationToken);
    }

    internal async Task<BoardProjectionData> PersistSnapshotDeltaAndReadBoardAsync(
        string meetingId,
        string source,
        DateTimeOffset capturedAt,
        IReadOnlyList<ParticipantSnapshotEntry> presentEntries,
        bool hasPreviousPresence,
        SnapshotDelta delta,
        IReadOnlyList<ParticipantEvent> derivedEvents,
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliases,
        string attendanceDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await PersistSnapshotDeltaOnConnectionAsync(
            connection,
            meetingId,
            source,
            capturedAt,
            presentEntries.Count,
            hasPreviousPresence,
            delta,
            derivedEvents,
            cancellationToken);

        return await ReadBoardProjectionOnConnectionAsync(
            connection,
            meetingId,
            attendanceDate,
            roster,
            aliases,
            knownPresenceSource: source,
            knownPresenceEntries: presentEntries,
            cancellationToken);
    }

    internal async Task<BoardProjectionData> ReadBoardProjectionAsync(
        string meetingId,
        string attendanceDate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var roster = await ReadRosterPeopleOnConnectionAsync(connection, cancellationToken);
        var aliases = await ReadAliasMapOnConnectionAsync(connection, cancellationToken);
        return await ReadBoardProjectionOnConnectionAsync(
            connection,
            meetingId,
            attendanceDate,
            roster,
            aliases,
            knownPresenceSource: null,
            knownPresenceEntries: null,
            cancellationToken);
    }

    private static async Task PersistSnapshotDeltaOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        string source,
        DateTimeOffset capturedAt,
        int presentCount,
        bool hasPreviousPresence,
        SnapshotDelta delta,
        IReadOnlyList<ParticipantEvent> derivedEvents,
        CancellationToken cancellationToken)
    {
        var capturedAtIso = capturedAt.ToString("O");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (derivedEvents.Count > 0)
        {
            using var eventInsert = new ParticipantEventInsertCommand(connection, transaction);
            using var summaryWriter = new EventSummaryUpsertCommands(connection, transaction);
            long sequence = 0;
            foreach (var derivedEvent in derivedEvents)
            {
                await eventInsert.InsertAsync(derivedEvent, cancellationToken);
                await summaryWriter.ApplyEventAsync(derivedEvent, ++sequence, cancellationToken);
            }
        }

        if (delta.RemovedPresenceKeys.Count > 0)
        {
            await using var deletePresence = connection.CreateCommand();
            deletePresence.Transaction = transaction;
            deletePresence.CommandText =
                "DELETE FROM participant_presence_v2 WHERE meeting_id = $meetingId AND source = $source AND presence_key = $presenceKey;";
            deletePresence.Parameters.AddWithValue("$meetingId", meetingId);
            deletePresence.Parameters.AddWithValue("$source", source);
            var keyParam = deletePresence.Parameters.Add("$presenceKey", SqliteType.Text);
            deletePresence.Prepare();

            foreach (var removedKey in delta.RemovedPresenceKeys)
            {
                keyParam.Value = removedKey;
                await deletePresence.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (hasPreviousPresence)
        {
            // Update last_seen_at for all continuing rows in one statement rather than deleting and re-inserting them.
            await using var touchPresence = connection.CreateCommand();
            touchPresence.Transaction = transaction;
            touchPresence.CommandText =
                "UPDATE participant_presence_v2 SET last_seen_at = $capturedAt WHERE meeting_id = $meetingId AND source = $source;";
            touchPresence.Parameters.AddWithValue("$capturedAt", capturedAtIso);
            touchPresence.Parameters.AddWithValue("$meetingId", meetingId);
            touchPresence.Parameters.AddWithValue("$source", source);
            await touchPresence.ExecuteNonQueryAsync(cancellationToken);
        }

        if (delta.UpdatedEntries.Count > 0)
        {
            await using var updatePresence = connection.CreateCommand();
            updatePresence.Transaction = transaction;
            updatePresence.CommandText =
                "UPDATE participant_presence_v2 SET normalized_name = $normalizedName, display_name = $displayName, last_seen_at = $lastSeenAt, participant_email = $participantEmail, raw_display_name = $rawDisplayName, canonical_name = $canonicalName WHERE meeting_id = $meetingId AND source = $source AND presence_key = $presenceKey;";
            updatePresence.Parameters.AddWithValue("$meetingId", meetingId);
            updatePresence.Parameters.AddWithValue("$source", source);
            var keyParam = updatePresence.Parameters.Add("$presenceKey", SqliteType.Text);
            var normParam = updatePresence.Parameters.Add("$normalizedName", SqliteType.Text);
            var displayParam = updatePresence.Parameters.Add("$displayName", SqliteType.Text);
            var lastSeenParam = updatePresence.Parameters.Add("$lastSeenAt", SqliteType.Text);
            var emailParam = updatePresence.Parameters.Add("$participantEmail", SqliteType.Text);
            var rawParam = updatePresence.Parameters.Add("$rawDisplayName", SqliteType.Text);
            var canonicalParam = updatePresence.Parameters.Add("$canonicalName", SqliteType.Text);
            updatePresence.Prepare();

            foreach (var entry in delta.UpdatedEntries)
            {
                keyParam.Value = entry.PresenceKey ?? entry.NormalizedName;
                normParam.Value = entry.NormalizedName;
                displayParam.Value = entry.DisplayName;
                lastSeenParam.Value = entry.LastSeenAt.ToString("O");
                emailParam.Value = (object?)entry.ParticipantEmail ?? DBNull.Value;
                rawParam.Value = (object?)entry.RawDisplayName ?? DBNull.Value;
                canonicalParam.Value = (object?)entry.CanonicalName ?? DBNull.Value;
                await updatePresence.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        if (delta.AddedEntries.Count > 0)
        {
            await using var insertPresence = connection.CreateCommand();
            insertPresence.Transaction = transaction;
            insertPresence.CommandText =
                "INSERT INTO participant_presence_v2 (meeting_id, source, presence_key, normalized_name, display_name, first_seen_at, last_seen_at, participant_email, raw_display_name, canonical_name) VALUES ($meetingId, $source, $presenceKey, $normalizedName, $displayName, $firstSeenAt, $lastSeenAt, $participantEmail, $rawDisplayName, $canonicalName);";
            insertPresence.Parameters.AddWithValue("$meetingId", meetingId);
            insertPresence.Parameters.AddWithValue("$source", source);
            var keyParam = insertPresence.Parameters.Add("$presenceKey", SqliteType.Text);
            var normParam = insertPresence.Parameters.Add("$normalizedName", SqliteType.Text);
            var displayParam = insertPresence.Parameters.Add("$displayName", SqliteType.Text);
            var firstSeenParam = insertPresence.Parameters.Add("$firstSeenAt", SqliteType.Text);
            var lastSeenParam = insertPresence.Parameters.Add("$lastSeenAt", SqliteType.Text);
            var emailParam = insertPresence.Parameters.Add("$participantEmail", SqliteType.Text);
            var rawParam = insertPresence.Parameters.Add("$rawDisplayName", SqliteType.Text);
            var canonicalParam = insertPresence.Parameters.Add("$canonicalName", SqliteType.Text);
            insertPresence.Prepare();

            foreach (var entry in delta.AddedEntries)
            {
                keyParam.Value = entry.PresenceKey ?? entry.NormalizedName;
                normParam.Value = entry.NormalizedName;
                displayParam.Value = entry.DisplayName;
                firstSeenParam.Value = entry.FirstSeenAt.ToString("O");
                lastSeenParam.Value = entry.LastSeenAt.ToString("O");
                emailParam.Value = (object?)entry.ParticipantEmail ?? DBNull.Value;
                rawParam.Value = (object?)entry.RawDisplayName ?? DBNull.Value;
                canonicalParam.Value = (object?)entry.CanonicalName ?? DBNull.Value;
                await insertPresence.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var upsertSnapshot = connection.CreateCommand())
        {
            upsertSnapshot.Transaction = transaction;
            upsertSnapshot.CommandText =
                "INSERT INTO participant_snapshots (meeting_id, source, captured_at, present_count) VALUES ($meetingId, $source, $capturedAt, $presentCount) ON CONFLICT(meeting_id, source) DO UPDATE SET captured_at = excluded.captured_at, present_count = excluded.present_count;";
            upsertSnapshot.Parameters.AddWithValue("$meetingId", meetingId);
            upsertSnapshot.Parameters.AddWithValue("$source", source);
            upsertSnapshot.Parameters.AddWithValue("$capturedAt", capturedAtIso);
            upsertSnapshot.Parameters.AddWithValue("$presentCount", presentCount);
            await upsertSnapshot.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<BoardProjectionData> ReadBoardProjectionOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        string attendanceDate,
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliases,
        string? knownPresenceSource,
        IReadOnlyList<ParticipantSnapshotEntry>? knownPresenceEntries,
        CancellationToken cancellationToken)
    {
        var snapshots = await ReadParticipantSnapshotSourcesOnConnectionAsync(connection, meetingId, cancellationToken);
        var zoomSnapshot = snapshots.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Source, ZoomLiveSnapshotSource, StringComparison.Ordinal));
        var authoritativeSnapshots = zoomSnapshot is null ? snapshots : new[] { zoomSnapshot };

        var currentPresence = new List<(string Source, ParticipantSnapshotEntry Entry)>();
        foreach (var snapshot in authoritativeSnapshots)
        {
            if (knownPresenceEntries is not null
                && string.Equals(snapshot.Source, knownPresenceSource, StringComparison.Ordinal))
            {
                foreach (var entry in knownPresenceEntries)
                {
                    currentPresence.Add((snapshot.Source, entry));
                }
            }
            else
            {
                var entries = await ReadParticipantPresenceOnConnectionAsync(connection, meetingId, snapshot.Source, cancellationToken);
                foreach (var entry in entries)
                {
                    currentPresence.Add((snapshot.Source, entry));
                }
            }
        }

        var exclusions = await ReadMeetingExclusionsOnConnectionAsync(connection, meetingId, attendanceDate, cancellationToken);
        var reviews = await ReadMeetingReviewsOnConnectionAsync(connection, meetingId, cancellationToken);
        var meetingMatches = await ReadMeetingConnectionMatchesOnConnectionAsync(connection, meetingId, cancellationToken);
        var personSummaries = await ReadPersonEventSummariesOnConnectionAsync(connection, meetingId, cancellationToken);
        var nameSummaries = await ReadNameEventSummariesOnConnectionAsync(connection, meetingId, cancellationToken);
        var recentEvents = await ReadRecentParticipantEventsOnConnectionAsync(connection, meetingId, RecentEventsLimit, cancellationToken);

        IReadOnlyList<ParticipantEvent> matchedConnectionEvents = Array.Empty<ParticipantEvent>();
        IReadOnlyList<ParticipantEvent> affectedPersonEvents = Array.Empty<ParticipantEvent>();
        IReadOnlyList<ParticipantEvent> affectedNameEvents = Array.Empty<ParticipantEvent>();

        if (meetingMatches.Count > 0)
        {
            matchedConnectionEvents = await ReadEventsForMatchedConnectionsOnConnectionAsync(
                connection,
                meetingId,
                meetingMatches,
                cancellationToken);

            var affectedPersonIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in meetingMatches)
            {
                if (!string.IsNullOrWhiteSpace(match.RosterPersonId))
                {
                    affectedPersonIds.Add(match.RosterPersonId);
                }
            }
            foreach (var evt in matchedConnectionEvents)
            {
                if (!string.IsNullOrWhiteSpace(evt.MatchedRosterPersonId))
                {
                    affectedPersonIds.Add(evt.MatchedRosterPersonId);
                }
            }

            if (affectedPersonIds.Count > 0)
            {
                affectedPersonEvents = await ReadEventsForRosterPeopleOnConnectionAsync(
                    connection,
                    meetingId,
                    affectedPersonIds,
                    cancellationToken);
            }

            if (snapshots.Count == 0 && matchedConnectionEvents.Count > 0)
            {
                var affectedNames = matchedConnectionEvents
                    .Select(evt => evt.NormalizedParticipantName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (affectedNames.Length > 0)
                {
                    affectedNameEvents = await ReadEventsForNormalizedNamesOnConnectionAsync(
                        connection,
                        meetingId,
                        affectedNames,
                        cancellationToken);
                }
            }
        }

        return new BoardProjectionData(
            Roster: roster,
            Aliases: aliases,
            SnapshotSources: snapshots,
            CurrentPresence: currentPresence,
            Exclusions: exclusions,
            Reviews: reviews,
            MeetingMatches: meetingMatches,
            PersonSummaries: personSummaries,
            NameSummaries: nameSummaries,
            RecentEvents: recentEvents,
            MatchedConnectionEvents: matchedConnectionEvents,
            AffectedPersonEvents: affectedPersonEvents,
            AffectedNameEvents: affectedNameEvents);
    }

    private static async Task<IReadOnlyList<RosterPerson>> ReadRosterPeopleOnConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, sequence, name, normalized_name, email, phone, organization, aliases_json, group_name FROM roster_people ORDER BY CAST(sequence AS INTEGER);";

        var people = new List<RosterPerson>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var aliasesJson = reader.GetString(7);
            var aliases = aliasesJson == "[]"
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(aliasesJson) ?? Array.Empty<string>();
            people.Add(new RosterPerson(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                aliases,
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8)));
        }

        return people;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadAliasMapOnConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
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

    private static async Task<IReadOnlyList<ParticipantSnapshotEntry>> ReadParticipantPresenceOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        string source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT normalized_name, display_name, first_seen_at, last_seen_at, participant_email, presence_key, raw_display_name, canonical_name FROM participant_presence_v2 WHERE meeting_id = $meetingId AND source = $source;";
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
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }

        return entries;
    }

    private static async Task<IReadOnlyList<ParticipantSnapshotSourceState>> ReadParticipantSnapshotSourcesOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT meeting_id, source, captured_at, present_count FROM participant_snapshots WHERE meeting_id = $meetingId ORDER BY captured_at DESC;";
        command.Parameters.AddWithValue("$meetingId", meetingId);

        var snapshots = new List<ParticipantSnapshotSourceState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(new ParticipantSnapshotSourceState(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                reader.GetInt32(3)));
        }

        return snapshots;
    }

    private static async Task<IReadOnlySet<string>> ReadMeetingExclusionsOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        string attendanceDate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT roster_person_id FROM meeting_exclusions WHERE meeting_id = $meetingId AND attendance_date = $date;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$date", attendanceDate);

        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<IReadOnlyList<MeetingReviewDecision>> ReadMeetingReviewsOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT subject_key, kind, status, evidence_token, updated_at FROM meeting_reviews WHERE meeting_id = $meetingId;";
        command.Parameters.AddWithValue("$meetingId", meetingId);

        var result = new List<MeetingReviewDecision>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MeetingReviewDecision(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return result;
    }

    private static async Task<IReadOnlyList<MeetingConnectionMatch>> ReadMeetingConnectionMatchesOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT source, presence_key, roster_person_id, observed_name, observed_email, first_seen_at, observed_at FROM meeting_connection_observations WHERE meeting_id = $meetingId;";
        command.Parameters.AddWithValue("$meetingId", meetingId);

        var result = new List<MeetingConnectionMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MeetingConnectionMatch(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                DateTimeOffset.Parse(reader.GetString(6))));
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<string, PersonEventSummary>> ReadPersonEventSummariesOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT roster_person_id, event_count, join_count, last_joined_at, last_left_at, last_non_rename_event_type, max_confidence, confidence_order FROM meeting_person_event_summary WHERE meeting_id = $meetingId;";
        command.Parameters.AddWithValue("$meetingId", meetingId);

        var result = new Dictionary<string, PersonEventSummary>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var personId = reader.GetString(0);
            result[personId] = new PersonEventSummary(
                RosterPersonId: personId,
                EventCount: reader.GetInt32(1),
                JoinCount: reader.GetInt32(2),
                LastJoinedAt: reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)),
                LastLeftAt: reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
                LastNonRenameEventType: reader.IsDBNull(5) ? null : Enum.Parse<ParticipantEventType>(reader.GetString(5)),
                MaxConfidence: (MatchConfidence)reader.GetInt32(6),
                ConfidenceOrder: reader.GetString(7));
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<string, NameEventSummary>> ReadNameEventSummariesOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT normalized_participant_name, total_event_count, unmatched_event_count, unmatched_latest_name, unmatched_latest_event_type, unmatched_latest_occurred_at FROM meeting_name_event_summary WHERE meeting_id = $meetingId;";
        command.Parameters.AddWithValue("$meetingId", meetingId);

        var result = new Dictionary<string, NameEventSummary>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var normalizedName = reader.GetString(0);
            result[normalizedName] = new NameEventSummary(
                NormalizedParticipantName: normalizedName,
                TotalEventCount: reader.GetInt32(1),
                UnmatchedEventCount: reader.GetInt32(2),
                UnmatchedLatestName: reader.IsDBNull(3) ? null : reader.GetString(3),
                UnmatchedLatestEventType: reader.IsDBNull(4) ? null : Enum.Parse<ParticipantEventType>(reader.GetString(4)),
                UnmatchedLatestOccurredAt: reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<ParticipantEvent>> ReadRecentParticipantEventsOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name FROM participant_events WHERE meeting_id = $meetingId ORDER BY occurred_at_ticks DESC, rowid ASC LIMIT $limit;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<ParticipantEvent>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadParticipantEvent(reader, offset: 0));
        }

        return events;
    }

    private static async Task<IReadOnlyList<ParticipantEvent>> ReadEventsForMatchedConnectionsOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        IReadOnlyList<MeetingConnectionMatch> matches,
        CancellationToken cancellationToken)
    {
        var distinctKeys = matches
            .Select(match => (match.Source, match.PresenceKey))
            .Distinct()
            .ToArray();
        var events = new List<ParticipantEvent>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        await using var byPresenceKey = connection.CreateCommand();
        byPresenceKey.CommandText =
            "SELECT id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name FROM participant_events WHERE meeting_id = $meetingId AND source = $source AND presence_key = $presenceKey ORDER BY occurred_at_ticks ASC, rowid ASC;";
        byPresenceKey.Parameters.AddWithValue("$meetingId", meetingId);
        var sourceParam = byPresenceKey.Parameters.Add("$source", SqliteType.Text);
        var keyParam = byPresenceKey.Parameters.Add("$presenceKey", SqliteType.Text);
        byPresenceKey.Prepare();

        await using var byNullPresenceKey = connection.CreateCommand();
        byNullPresenceKey.CommandText =
            "SELECT id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name FROM participant_events WHERE meeting_id = $meetingId AND source = $source AND presence_key IS NULL AND normalized_participant_name = $presenceKey ORDER BY occurred_at_ticks ASC, rowid ASC;";
        byNullPresenceKey.Parameters.AddWithValue("$meetingId", meetingId);
        var nullSourceParam = byNullPresenceKey.Parameters.Add("$source", SqliteType.Text);
        var nullKeyParam = byNullPresenceKey.Parameters.Add("$presenceKey", SqliteType.Text);
        byNullPresenceKey.Prepare();

        foreach (var (source, key) in distinctKeys)
        {
            sourceParam.Value = source;
            keyParam.Value = key;
            await using (var reader = await byPresenceKey.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var evt = ReadParticipantEvent(reader, offset: 0);
                    if (seenIds.Add(evt.Id))
                    {
                        events.Add(evt);
                    }
                }
            }

            nullSourceParam.Value = source;
            nullKeyParam.Value = key;
            await using (var reader = await byNullPresenceKey.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var evt = ReadParticipantEvent(reader, offset: 0);
                    if (seenIds.Add(evt.Id))
                    {
                        events.Add(evt);
                    }
                }
            }
        }

        return events;
    }

    private static async Task<IReadOnlyList<ParticipantEvent>> ReadEventsForRosterPeopleOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        IReadOnlySet<string> personIds,
        CancellationToken cancellationToken)
    {
        var events = new List<ParticipantEvent>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name FROM participant_events WHERE meeting_id = $meetingId AND matched_roster_person_id = $personId ORDER BY occurred_at_ticks ASC, rowid ASC;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        var personParam = command.Parameters.Add("$personId", SqliteType.Text);
        command.Prepare();

        foreach (var personId in personIds)
        {
            personParam.Value = personId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(ReadParticipantEvent(reader, offset: 0));
            }
        }

        return events;
    }

    private static async Task<IReadOnlyList<ParticipantEvent>> ReadEventsForNormalizedNamesOnConnectionAsync(
        SqliteConnection connection,
        string meetingId,
        IReadOnlyList<string> normalizedNames,
        CancellationToken cancellationToken)
    {
        var events = new List<ParticipantEvent>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name FROM participant_events WHERE meeting_id = $meetingId AND normalized_participant_name = $normalizedName ORDER BY occurred_at_ticks ASC, rowid ASC;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        var nameParam = command.Parameters.Add("$normalizedName", SqliteType.Text);
        command.Prepare();

        foreach (var name in normalizedNames)
        {
            nameParam.Value = name;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(ReadParticipantEvent(reader, offset: 0));
            }
        }

        return events;
    }

    private static ParticipantEvent ReadParticipantEvent(SqliteDataReader reader, int offset)
        => new(
            reader.GetString(offset + 0),
            reader.GetString(offset + 1),
            DateTimeOffset.Parse(reader.GetString(offset + 2)),
            Enum.Parse<ParticipantEventType>(reader.GetString(offset + 3)),
            reader.GetString(offset + 4),
            reader.GetString(offset + 5),
            reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
            Enum.Parse<MatchConfidence>(reader.GetString(offset + 7)),
            reader.IsDBNull(offset + 8) ? null : reader.GetString(offset + 8),
            reader.GetString(offset + 9),
            reader.GetString(offset + 10),
            reader.IsDBNull(offset + 11) ? null : reader.GetString(offset + 11),
            reader.IsDBNull(offset + 12) ? null : reader.GetString(offset + 12),
            reader.IsDBNull(offset + 13) ? null : reader.GetString(offset + 13),
            reader.IsDBNull(offset + 14) ? null : reader.GetString(offset + 14),
            reader.IsDBNull(offset + 15) ? null : reader.GetString(offset + 15));

    private sealed class ParticipantEventInsertCommand : IDisposable
    {
        private readonly SqliteCommand _command;
        private readonly SqliteParameter _id;
        private readonly SqliteParameter _meetingId;
        private readonly SqliteParameter _occurredAt;
        private readonly SqliteParameter _occurredAtTicks;
        private readonly SqliteParameter _eventType;
        private readonly SqliteParameter _participantName;
        private readonly SqliteParameter _normalizedParticipantName;
        private readonly SqliteParameter _participantEmail;
        private readonly SqliteParameter _confidence;
        private readonly SqliteParameter _matchedRosterPersonId;
        private readonly SqliteParameter _source;
        private readonly SqliteParameter _rawPayload;
        private readonly SqliteParameter _presenceKey;
        private readonly SqliteParameter _rawParticipantName;
        private readonly SqliteParameter _canonicalParticipantName;
        private readonly SqliteParameter _previousParticipantName;
        private readonly SqliteParameter _previousRawParticipantName;

        public ParticipantEventInsertCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            _command = connection.CreateCommand();
            _command.Transaction = transaction;
            _command.CommandText =
                "INSERT INTO participant_events (id, meeting_id, occurred_at, occurred_at_ticks, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name) VALUES ($id, $meetingId, $occurredAt, $occurredAtTicks, $eventType, $participantName, $normalizedParticipantName, $participantEmail, $confidence, $matchedRosterPersonId, $source, $rawPayload, $presenceKey, $rawParticipantName, $canonicalParticipantName, $previousParticipantName, $previousRawParticipantName);";
            _id = _command.Parameters.Add("$id", SqliteType.Text);
            _meetingId = _command.Parameters.Add("$meetingId", SqliteType.Text);
            _occurredAt = _command.Parameters.Add("$occurredAt", SqliteType.Text);
            _occurredAtTicks = _command.Parameters.Add("$occurredAtTicks", SqliteType.Integer);
            _eventType = _command.Parameters.Add("$eventType", SqliteType.Text);
            _participantName = _command.Parameters.Add("$participantName", SqliteType.Text);
            _normalizedParticipantName = _command.Parameters.Add("$normalizedParticipantName", SqliteType.Text);
            _participantEmail = _command.Parameters.Add("$participantEmail", SqliteType.Text);
            _confidence = _command.Parameters.Add("$confidence", SqliteType.Text);
            _matchedRosterPersonId = _command.Parameters.Add("$matchedRosterPersonId", SqliteType.Text);
            _source = _command.Parameters.Add("$source", SqliteType.Text);
            _rawPayload = _command.Parameters.Add("$rawPayload", SqliteType.Text);
            _presenceKey = _command.Parameters.Add("$presenceKey", SqliteType.Text);
            _rawParticipantName = _command.Parameters.Add("$rawParticipantName", SqliteType.Text);
            _canonicalParticipantName = _command.Parameters.Add("$canonicalParticipantName", SqliteType.Text);
            _previousParticipantName = _command.Parameters.Add("$previousParticipantName", SqliteType.Text);
            _previousRawParticipantName = _command.Parameters.Add("$previousRawParticipantName", SqliteType.Text);
            _command.Prepare();
        }

        public Task InsertAsync(ParticipantEvent evt, CancellationToken cancellationToken)
        {
            _id.Value = evt.Id;
            _meetingId.Value = evt.MeetingId;
            _occurredAt.Value = evt.OccurredAt.ToString("O");
            _occurredAtTicks.Value = evt.OccurredAt.UtcTicks;
            _eventType.Value = evt.EventType.ToString();
            _participantName.Value = evt.ParticipantName;
            _normalizedParticipantName.Value = evt.NormalizedParticipantName;
            _participantEmail.Value = (object?)evt.ParticipantEmail ?? DBNull.Value;
            _confidence.Value = evt.Confidence.ToString();
            _matchedRosterPersonId.Value = (object?)evt.MatchedRosterPersonId ?? DBNull.Value;
            _source.Value = evt.Source;
            _rawPayload.Value = evt.RawPayload;
            _presenceKey.Value = (object?)evt.PresenceKey ?? DBNull.Value;
            _rawParticipantName.Value = (object?)evt.RawParticipantName ?? DBNull.Value;
            _canonicalParticipantName.Value = (object?)evt.CanonicalParticipantName ?? DBNull.Value;
            _previousParticipantName.Value = (object?)evt.PreviousParticipantName ?? DBNull.Value;
            _previousRawParticipantName.Value = (object?)evt.PreviousRawParticipantName ?? DBNull.Value;
            return _command.ExecuteNonQueryAsync(cancellationToken);
        }

        public void Dispose() => _command.Dispose();
    }

    private sealed class EventSummaryUpsertCommands : IDisposable
    {
        private readonly SqliteCommand _personCommand;
        private readonly SqliteParameter _pMeetingId;
        private readonly SqliteParameter _pPersonId;
        private readonly SqliteParameter _pJoinCount;
        private readonly SqliteParameter _pLastJoinedAt;
        private readonly SqliteParameter _pLastJoinedTicks;
        private readonly SqliteParameter _pLastLeftAt;
        private readonly SqliteParameter _pLastLeftTicks;
        private readonly SqliteParameter _pLastNonRenameType;
        private readonly SqliteParameter _pLastNonRenameTicks;
        private readonly SqliteParameter _pLastNonRenameRowId;
        private readonly SqliteParameter _pMaxConfidence;
        private readonly SqliteParameter _pConfidenceName;

        private readonly SqliteCommand _nameCommand;
        private readonly SqliteParameter _nMeetingId;
        private readonly SqliteParameter _nNormalizedName;
        private readonly SqliteParameter _nUnmatchedCount;
        private readonly SqliteParameter _nLatestName;
        private readonly SqliteParameter _nLatestEventType;
        private readonly SqliteParameter _nLatestOccurredAt;
        private readonly SqliteParameter _nLatestTicks;
        private readonly SqliteParameter _nLatestRowId;

        public EventSummaryUpsertCommands(SqliteConnection connection, SqliteTransaction transaction)
        {
            _personCommand = connection.CreateCommand();
            _personCommand.Transaction = transaction;
            _personCommand.CommandText = """
                INSERT INTO meeting_person_event_summary (
                    meeting_id, roster_person_id, event_count, join_count,
                    last_joined_at, last_joined_ticks, last_left_at, last_left_ticks,
                    last_non_rename_event_type, last_non_rename_ticks, last_non_rename_rowid,
                    max_confidence, confidence_order
                ) VALUES (
                    $meetingId, $personId, 1, $joinCount,
                    $lastJoinedAt, $lastJoinedTicks, $lastLeftAt, $lastLeftTicks,
                    $lastNonRenameType, $lastNonRenameTicks, $lastNonRenameRowId,
                    $maxConfidence, $confidenceName
                )
                ON CONFLICT(meeting_id, roster_person_id) DO UPDATE SET
                    event_count = meeting_person_event_summary.event_count + 1,
                    join_count = meeting_person_event_summary.join_count + excluded.join_count,
                    last_joined_at = CASE
                        WHEN excluded.last_joined_ticks IS NOT NULL
                            AND (meeting_person_event_summary.last_joined_ticks IS NULL OR excluded.last_joined_ticks > meeting_person_event_summary.last_joined_ticks)
                        THEN excluded.last_joined_at ELSE meeting_person_event_summary.last_joined_at END,
                    last_joined_ticks = CASE
                        WHEN excluded.last_joined_ticks IS NOT NULL
                            AND (meeting_person_event_summary.last_joined_ticks IS NULL OR excluded.last_joined_ticks > meeting_person_event_summary.last_joined_ticks)
                        THEN excluded.last_joined_ticks ELSE meeting_person_event_summary.last_joined_ticks END,
                    last_left_at = CASE
                        WHEN excluded.last_left_ticks IS NOT NULL
                            AND (meeting_person_event_summary.last_left_ticks IS NULL OR excluded.last_left_ticks > meeting_person_event_summary.last_left_ticks)
                        THEN excluded.last_left_at ELSE meeting_person_event_summary.last_left_at END,
                    last_left_ticks = CASE
                        WHEN excluded.last_left_ticks IS NOT NULL
                            AND (meeting_person_event_summary.last_left_ticks IS NULL OR excluded.last_left_ticks > meeting_person_event_summary.last_left_ticks)
                        THEN excluded.last_left_ticks ELSE meeting_person_event_summary.last_left_ticks END,
                    last_non_rename_event_type = CASE
                        WHEN excluded.last_non_rename_ticks IS NOT NULL AND (
                            meeting_person_event_summary.last_non_rename_ticks IS NULL
                            OR excluded.last_non_rename_ticks > meeting_person_event_summary.last_non_rename_ticks
                            OR (excluded.last_non_rename_ticks = meeting_person_event_summary.last_non_rename_ticks
                                AND excluded.last_non_rename_rowid >= meeting_person_event_summary.last_non_rename_rowid)
                        ) THEN excluded.last_non_rename_event_type ELSE meeting_person_event_summary.last_non_rename_event_type END,
                    last_non_rename_ticks = CASE
                        WHEN excluded.last_non_rename_ticks IS NOT NULL AND (
                            meeting_person_event_summary.last_non_rename_ticks IS NULL
                            OR excluded.last_non_rename_ticks > meeting_person_event_summary.last_non_rename_ticks
                            OR (excluded.last_non_rename_ticks = meeting_person_event_summary.last_non_rename_ticks
                                AND excluded.last_non_rename_rowid >= meeting_person_event_summary.last_non_rename_rowid)
                        ) THEN excluded.last_non_rename_ticks ELSE meeting_person_event_summary.last_non_rename_ticks END,
                    last_non_rename_rowid = CASE
                        WHEN excluded.last_non_rename_ticks IS NOT NULL AND (
                            meeting_person_event_summary.last_non_rename_ticks IS NULL
                            OR excluded.last_non_rename_ticks > meeting_person_event_summary.last_non_rename_ticks
                            OR (excluded.last_non_rename_ticks = meeting_person_event_summary.last_non_rename_ticks
                                AND excluded.last_non_rename_rowid >= meeting_person_event_summary.last_non_rename_rowid)
                        ) THEN excluded.last_non_rename_rowid ELSE meeting_person_event_summary.last_non_rename_rowid END,
                    max_confidence = MAX(meeting_person_event_summary.max_confidence, excluded.max_confidence),
                    confidence_order = CASE
                        WHEN instr(', ' || meeting_person_event_summary.confidence_order || ', ', ', ' || excluded.confidence_order || ', ') > 0
                        THEN meeting_person_event_summary.confidence_order
                        ELSE meeting_person_event_summary.confidence_order || ', ' || excluded.confidence_order END;
                """;
            _pMeetingId = _personCommand.Parameters.Add("$meetingId", SqliteType.Text);
            _pPersonId = _personCommand.Parameters.Add("$personId", SqliteType.Text);
            _pJoinCount = _personCommand.Parameters.Add("$joinCount", SqliteType.Integer);
            _pLastJoinedAt = _personCommand.Parameters.Add("$lastJoinedAt", SqliteType.Text);
            _pLastJoinedTicks = _personCommand.Parameters.Add("$lastJoinedTicks", SqliteType.Integer);
            _pLastLeftAt = _personCommand.Parameters.Add("$lastLeftAt", SqliteType.Text);
            _pLastLeftTicks = _personCommand.Parameters.Add("$lastLeftTicks", SqliteType.Integer);
            _pLastNonRenameType = _personCommand.Parameters.Add("$lastNonRenameType", SqliteType.Text);
            _pLastNonRenameTicks = _personCommand.Parameters.Add("$lastNonRenameTicks", SqliteType.Integer);
            _pLastNonRenameRowId = _personCommand.Parameters.Add("$lastNonRenameRowId", SqliteType.Integer);
            _pMaxConfidence = _personCommand.Parameters.Add("$maxConfidence", SqliteType.Integer);
            _pConfidenceName = _personCommand.Parameters.Add("$confidenceName", SqliteType.Text);
            _personCommand.Prepare();

            _nameCommand = connection.CreateCommand();
            _nameCommand.Transaction = transaction;
            _nameCommand.CommandText = """
                INSERT INTO meeting_name_event_summary (
                    meeting_id, normalized_participant_name, total_event_count,
                    unmatched_event_count, unmatched_latest_name, unmatched_latest_event_type,
                    unmatched_latest_occurred_at, unmatched_latest_ticks, unmatched_latest_rowid
                ) VALUES (
                    $meetingId, $normalizedName, 1,
                    $unmatchedCount, $latestName, $latestEventType,
                    $latestOccurredAt, $latestTicks, $latestRowId
                )
                ON CONFLICT(meeting_id, normalized_participant_name) DO UPDATE SET
                    total_event_count = meeting_name_event_summary.total_event_count + 1,
                    unmatched_event_count = meeting_name_event_summary.unmatched_event_count + excluded.unmatched_event_count,
                    unmatched_latest_name = CASE
                        WHEN excluded.unmatched_latest_ticks IS NOT NULL AND (
                            meeting_name_event_summary.unmatched_latest_ticks IS NULL
                            OR excluded.unmatched_latest_ticks > meeting_name_event_summary.unmatched_latest_ticks
                            OR (excluded.unmatched_latest_ticks = meeting_name_event_summary.unmatched_latest_ticks
                                AND excluded.unmatched_latest_rowid >= meeting_name_event_summary.unmatched_latest_rowid)
                        ) THEN excluded.unmatched_latest_name ELSE meeting_name_event_summary.unmatched_latest_name END,
                    unmatched_latest_event_type = CASE
                        WHEN excluded.unmatched_latest_ticks IS NOT NULL AND (
                            meeting_name_event_summary.unmatched_latest_ticks IS NULL
                            OR excluded.unmatched_latest_ticks > meeting_name_event_summary.unmatched_latest_ticks
                            OR (excluded.unmatched_latest_ticks = meeting_name_event_summary.unmatched_latest_ticks
                                AND excluded.unmatched_latest_rowid >= meeting_name_event_summary.unmatched_latest_rowid)
                        ) THEN excluded.unmatched_latest_event_type ELSE meeting_name_event_summary.unmatched_latest_event_type END,
                    unmatched_latest_occurred_at = CASE
                        WHEN excluded.unmatched_latest_ticks IS NOT NULL AND (
                            meeting_name_event_summary.unmatched_latest_ticks IS NULL
                            OR excluded.unmatched_latest_ticks > meeting_name_event_summary.unmatched_latest_ticks
                            OR (excluded.unmatched_latest_ticks = meeting_name_event_summary.unmatched_latest_ticks
                                AND excluded.unmatched_latest_rowid >= meeting_name_event_summary.unmatched_latest_rowid)
                        ) THEN excluded.unmatched_latest_occurred_at ELSE meeting_name_event_summary.unmatched_latest_occurred_at END,
                    unmatched_latest_ticks = CASE
                        WHEN excluded.unmatched_latest_ticks IS NOT NULL AND (
                            meeting_name_event_summary.unmatched_latest_ticks IS NULL
                            OR excluded.unmatched_latest_ticks > meeting_name_event_summary.unmatched_latest_ticks
                            OR (excluded.unmatched_latest_ticks = meeting_name_event_summary.unmatched_latest_ticks
                                AND excluded.unmatched_latest_rowid >= meeting_name_event_summary.unmatched_latest_rowid)
                        ) THEN excluded.unmatched_latest_ticks ELSE meeting_name_event_summary.unmatched_latest_ticks END,
                    unmatched_latest_rowid = CASE
                        WHEN excluded.unmatched_latest_ticks IS NOT NULL AND (
                            meeting_name_event_summary.unmatched_latest_ticks IS NULL
                            OR excluded.unmatched_latest_ticks > meeting_name_event_summary.unmatched_latest_ticks
                            OR (excluded.unmatched_latest_ticks = meeting_name_event_summary.unmatched_latest_ticks
                                AND excluded.unmatched_latest_rowid >= meeting_name_event_summary.unmatched_latest_rowid)
                        ) THEN excluded.unmatched_latest_rowid ELSE meeting_name_event_summary.unmatched_latest_rowid END;
                """;
            _nMeetingId = _nameCommand.Parameters.Add("$meetingId", SqliteType.Text);
            _nNormalizedName = _nameCommand.Parameters.Add("$normalizedName", SqliteType.Text);
            _nUnmatchedCount = _nameCommand.Parameters.Add("$unmatchedCount", SqliteType.Integer);
            _nLatestName = _nameCommand.Parameters.Add("$latestName", SqliteType.Text);
            _nLatestEventType = _nameCommand.Parameters.Add("$latestEventType", SqliteType.Text);
            _nLatestOccurredAt = _nameCommand.Parameters.Add("$latestOccurredAt", SqliteType.Text);
            _nLatestTicks = _nameCommand.Parameters.Add("$latestTicks", SqliteType.Integer);
            _nLatestRowId = _nameCommand.Parameters.Add("$latestRowId", SqliteType.Integer);
            _nameCommand.Prepare();
        }

        public async Task ApplyEventAsync(ParticipantEvent evt, long rowId, CancellationToken cancellationToken)
        {
            var occurredAtIso = evt.OccurredAt.ToString("O");
            var ticks = evt.OccurredAt.UtcTicks;

            var isUnmatched = string.IsNullOrWhiteSpace(evt.MatchedRosterPersonId);
            if (!isUnmatched)
            {
                _pMeetingId.Value = evt.MeetingId;
                _pPersonId.Value = evt.MatchedRosterPersonId!;
                _pJoinCount.Value = evt.EventType == ParticipantEventType.Joined ? 1 : 0;
                _pLastJoinedAt.Value = evt.EventType == ParticipantEventType.Joined ? occurredAtIso : DBNull.Value;
                _pLastJoinedTicks.Value = evt.EventType == ParticipantEventType.Joined ? ticks : DBNull.Value;
                _pLastLeftAt.Value = evt.EventType == ParticipantEventType.Left ? occurredAtIso : DBNull.Value;
                _pLastLeftTicks.Value = evt.EventType == ParticipantEventType.Left ? ticks : DBNull.Value;
                var isNonRename = evt.EventType != ParticipantEventType.NameChanged;
                _pLastNonRenameType.Value = isNonRename ? evt.EventType.ToString() : DBNull.Value;
                _pLastNonRenameTicks.Value = isNonRename ? ticks : DBNull.Value;
                _pLastNonRenameRowId.Value = isNonRename ? rowId : 0L;
                _pMaxConfidence.Value = (int)evt.Confidence;
                _pConfidenceName.Value = evt.Confidence.ToString();
                await _personCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (isUnmatched)
            {
                _nMeetingId.Value = evt.MeetingId;
                _nNormalizedName.Value = evt.NormalizedParticipantName;
                _nUnmatchedCount.Value = 1;
                _nLatestName.Value = evt.ParticipantName;
                _nLatestEventType.Value = evt.EventType.ToString();
                _nLatestOccurredAt.Value = occurredAtIso;
                _nLatestTicks.Value = ticks;
                _nLatestRowId.Value = rowId;
                await _nameCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        public void Dispose()
        {
            _personCommand.Dispose();
            _nameCommand.Dispose();
        }
    }
}
