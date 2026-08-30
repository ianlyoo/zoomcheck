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

        // The app opens short-lived connections for each repository operation. Disabling
        // pooling ensures those connections release the database file immediately on
        // Windows as well, which is important for clean shutdowns and portable builds.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Pooling = false
        }.ToString();
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
                raw_payload TEXT NOT NULL,
                presence_key TEXT NULL,
                raw_participant_name TEXT NULL,
                canonical_participant_name TEXT NULL,
                previous_participant_name TEXT NULL,
                previous_raw_participant_name TEXT NULL
            );
            """
            ,
            """
            CREATE TABLE IF NOT EXISTS participant_snapshots (
                meeting_id TEXT NOT NULL,
                source TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                present_count INTEGER NOT NULL,
                PRIMARY KEY (meeting_id, source)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS participant_presence_v2 (
                meeting_id TEXT NOT NULL,
                source TEXT NOT NULL,
                presence_key TEXT NOT NULL,
                normalized_name TEXT NOT NULL,
                display_name TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                participant_email TEXT NULL,
                raw_display_name TEXT NULL,
                canonical_name TEXT NULL,
                PRIMARY KEY (meeting_id, source, presence_key)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                migration_id TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """
        };

        foreach (var sql in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Older databases predate the name-change / canonical-name columns. Add them in place so
        // existing attendance history and the presence-v2 migration below keep working.
        foreach (var (table, column) in new[]
        {
            ("participant_events", "presence_key"),
            ("participant_events", "raw_participant_name"),
            ("participant_events", "canonical_participant_name"),
            ("participant_events", "previous_participant_name"),
            ("participant_events", "previous_raw_participant_name"),
            ("participant_presence_v2", "raw_display_name"),
            ("participant_presence_v2", "canonical_name")
        })
        {
            await EnsureColumnExistsAsync(connection, table, column, "TEXT NULL", cancellationToken);
        }

        if (await TableExistsAsync(connection, "participant_presence", cancellationToken))
        {
            await EnsureColumnExistsAsync(
                connection,
                tableName: "participant_presence",
                columnName: "participant_email",
                definition: "TEXT NULL",
                cancellationToken);

            await using var migratePresence = connection.CreateCommand();
            migratePresence.CommandText =
                "INSERT OR IGNORE INTO participant_presence_v2 (meeting_id, source, presence_key, normalized_name, display_name, first_seen_at, last_seen_at, participant_email) SELECT meeting_id, source, normalized_name, normalized_name, display_name, first_seen_at, last_seen_at, participant_email FROM participant_presence WHERE NOT EXISTS (SELECT 1 FROM schema_migrations WHERE migration_id = 'presence-v2'); " +
                "INSERT OR IGNORE INTO schema_migrations (migration_id, applied_at) VALUES ('presence-v2', $appliedAt);";
            migratePresence.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await migratePresence.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task ReplaceRosterAsync(RosterImportResult roster, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var text in new[]
        {
            "DELETE FROM roster_people;",
            "DELETE FROM roster_imports;"
        })
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

    public async Task<IReadOnlyList<ParticipantSnapshotSourceState>> GetParticipantSnapshotSourcesAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
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

    /// <summary>
    /// Atomically appends the derived join/leave events and replaces the presence set
    /// for the given (meeting, source) pair.
    /// </summary>
    public async Task ApplyParticipantSnapshotAsync(
        string meetingId,
        string source,
        DateTimeOffset capturedAt,
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
            clearPresence.CommandText = "DELETE FROM participant_presence_v2 WHERE meeting_id = $meetingId AND source = $source;";
            clearPresence.Parameters.AddWithValue("$meetingId", meetingId);
            clearPresence.Parameters.AddWithValue("$source", source);
            await clearPresence.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var entry in presentEntries)
        {
            await using var insertPresence = connection.CreateCommand();
            insertPresence.Transaction = transaction;
            insertPresence.CommandText =
                "INSERT INTO participant_presence_v2 (meeting_id, source, presence_key, normalized_name, display_name, first_seen_at, last_seen_at, participant_email, raw_display_name, canonical_name) VALUES ($meetingId, $source, $presenceKey, $normalizedName, $displayName, $firstSeenAt, $lastSeenAt, $participantEmail, $rawDisplayName, $canonicalName);";
            insertPresence.Parameters.AddWithValue("$meetingId", meetingId);
            insertPresence.Parameters.AddWithValue("$source", source);
            insertPresence.Parameters.AddWithValue("$presenceKey", entry.PresenceKey ?? entry.NormalizedName);
            insertPresence.Parameters.AddWithValue("$normalizedName", entry.NormalizedName);
            insertPresence.Parameters.AddWithValue("$displayName", entry.DisplayName);
            insertPresence.Parameters.AddWithValue("$firstSeenAt", entry.FirstSeenAt.ToString("O"));
            insertPresence.Parameters.AddWithValue("$lastSeenAt", entry.LastSeenAt.ToString("O"));
            insertPresence.Parameters.AddWithValue("$participantEmail", (object?)entry.ParticipantEmail ?? DBNull.Value);
            insertPresence.Parameters.AddWithValue("$rawDisplayName", (object?)entry.RawDisplayName ?? DBNull.Value);
            insertPresence.Parameters.AddWithValue("$canonicalName", (object?)entry.CanonicalName ?? DBNull.Value);
            await insertPresence.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsertSnapshot = connection.CreateCommand())
        {
            upsertSnapshot.Transaction = transaction;
            upsertSnapshot.CommandText =
                "INSERT INTO participant_snapshots (meeting_id, source, captured_at, present_count) VALUES ($meetingId, $source, $capturedAt, $presentCount) ON CONFLICT(meeting_id, source) DO UPDATE SET captured_at = excluded.captured_at, present_count = excluded.present_count;";
            upsertSnapshot.Parameters.AddWithValue("$meetingId", meetingId);
            upsertSnapshot.Parameters.AddWithValue("$source", source);
            upsertSnapshot.Parameters.AddWithValue("$capturedAt", capturedAt.ToString("O"));
            upsertSnapshot.Parameters.AddWithValue("$presentCount", presentEntries.Count);
            await upsertSnapshot.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ParticipantEvent>> GetParticipantEventsAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name FROM participant_events WHERE meeting_id = $meetingId ORDER BY occurred_at ASC, rowid ASC;";
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
                reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        return events;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({tableName});";

        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static void BindParticipantEvent(SqliteCommand command, ParticipantEvent participantEvent)
    {
        command.CommandText =
            "INSERT INTO participant_events (id, meeting_id, occurred_at, event_type, participant_name, normalized_participant_name, participant_email, confidence, matched_roster_person_id, source, raw_payload, presence_key, raw_participant_name, canonical_participant_name, previous_participant_name, previous_raw_participant_name) VALUES ($id, $meetingId, $occurredAt, $eventType, $participantName, $normalizedParticipantName, $participantEmail, $confidence, $matchedRosterPersonId, $source, $rawPayload, $presenceKey, $rawParticipantName, $canonicalParticipantName, $previousParticipantName, $previousRawParticipantName);";
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
        command.Parameters.AddWithValue("$presenceKey", (object?)participantEvent.PresenceKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawParticipantName", (object?)participantEvent.RawParticipantName ?? DBNull.Value);
        command.Parameters.AddWithValue("$canonicalParticipantName", (object?)participantEvent.CanonicalParticipantName ?? DBNull.Value);
        command.Parameters.AddWithValue("$previousParticipantName", (object?)participantEvent.PreviousParticipantName ?? DBNull.Value);
        command.Parameters.AddWithValue("$previousRawParticipantName", (object?)participantEvent.PreviousRawParticipantName ?? DBNull.Value);
    }
}
