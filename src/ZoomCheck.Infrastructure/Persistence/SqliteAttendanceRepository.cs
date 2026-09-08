using System.Text.Json;
using Microsoft.Data.Sqlite;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Persistence;

public sealed partial class SqliteAttendanceRepository
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
                aliases_json TEXT NOT NULL,
                group_name TEXT NOT NULL DEFAULT ''
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

        // Rosters gained an optional group column later. It is additive and non-null so existing
        // roster rows simply read back as ungrouped instead of requiring a re-import.
        await EnsureColumnExistsAsync(
            connection,
            tableName: "roster_people",
            columnName: "group_name",
            definition: "TEXT NOT NULL DEFAULT ''",
            cancellationToken);

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

        await NormalizeLegacyMeetingIdsAsync(connection, cancellationToken);
        await InitializeMeetingDecisionsAsync(connection, cancellationToken);
    }

    public async Task ReplaceRosterAsync(RosterImportResult roster, CancellationToken cancellationToken = default)
    {
        await ReplaceRosterWithReconciliationAsync(roster, cancellationToken);
    }

    /// <summary>
    /// Replaces the stored roster while preserving the persisted identity of people who are still
    /// present in the incoming roster, so that previously recorded participant events and manual
    /// aliases keep pointing at the same roster person.
    /// </summary>
    /// <remarks>
    /// Identity is reconciled conservatively: a retained person is recognised
    /// by a unique non-empty email (case-insensitive), otherwise by a unique normalized name. A key
    /// must be unique on both the stored side and the incoming side to be usable, so ambiguous
    /// identities are never guessed and are treated as new people instead.
    /// </remarks>
    public async Task<RosterReconciliationResult> ReplaceRosterWithReconciliationAsync(RosterImportResult roster, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existingPeople = await ReadRosterIdentitiesAsync(connection, transaction, cancellationToken);
        var reconciliation = ReconcileRosterIdentities(existingPeople, roster.People);

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
            var persistedId = reconciliation.ResolveId(person);
            await using var insertPerson = connection.CreateCommand();
            insertPerson.Transaction = transaction;
            insertPerson.CommandText =
                "INSERT INTO roster_people (id, import_id, sequence, name, normalized_name, email, phone, organization, aliases_json, group_name) VALUES ($id, $importId, $sequence, $name, $normalizedName, $email, $phone, $organization, $aliases, $group);";
            insertPerson.Parameters.AddWithValue("$id", persistedId);
            insertPerson.Parameters.AddWithValue("$importId", roster.ImportId);
            insertPerson.Parameters.AddWithValue("$sequence", person.Sequence);
            insertPerson.Parameters.AddWithValue("$name", person.Name);
            insertPerson.Parameters.AddWithValue("$normalizedName", person.NormalizedName);
            insertPerson.Parameters.AddWithValue("$email", person.Email);
            insertPerson.Parameters.AddWithValue("$phone", person.Phone);
            insertPerson.Parameters.AddWithValue("$organization", person.Organization);
            insertPerson.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(person.Aliases));
            insertPerson.Parameters.AddWithValue("$group", person.Group ?? string.Empty);
            await insertPerson.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return reconciliation;
    }

    public async Task<IReadOnlyList<RosterPerson>> GetRosterPeopleAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, sequence, name, normalized_name, email, phone, organization, aliases_json, group_name FROM roster_people ORDER BY CAST(sequence AS INTEGER);";

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
                aliases,
                reader.IsDBNull(8) ? string.Empty : reader.GetString(8)));
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

    private static async Task NormalizeLegacyMeetingIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var alreadyApplied = connection.CreateCommand())
        {
            alreadyApplied.CommandText =
                "SELECT 1 FROM schema_migrations WHERE migration_id = 'meeting-id-v1' LIMIT 1;";
            if (await alreadyApplied.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return;
            }
        }

        var meetingIds = new List<string>();
        await using (var findIds = connection.CreateCommand())
        {
            findIds.CommandText =
                "SELECT meeting_id FROM participant_events " +
                "UNION SELECT meeting_id FROM participant_snapshots " +
                "UNION SELECT meeting_id FROM participant_presence_v2;";
            await using var reader = await findIds.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                meetingIds.Add(reader.GetString(0));
            }
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var legacyMeetingId in meetingIds)
        {
            if (!MeetingIdNormalizer.TryNormalize(legacyMeetingId, out var normalizedMeetingId)
                || string.Equals(legacyMeetingId, normalizedMeetingId, StringComparison.Ordinal))
            {
                continue;
            }

            // Event ids are globally unique, so histories from both display forms can be merged.
            await ExecuteMigrationCommandAsync(
                connection,
                transaction,
                "UPDATE participant_events SET meeting_id = $normalized WHERE meeting_id = $legacy;",
                legacyMeetingId,
                normalizedMeetingId,
                cancellationToken);

            // Keep the newest snapshot for each source and move its matching presence set with it.
            var sources = new List<string>();
            await using (var findSources = connection.CreateCommand())
            {
                findSources.Transaction = transaction;
                findSources.CommandText =
                    "SELECT source FROM participant_snapshots WHERE meeting_id IN ($legacy, $normalized) " +
                    "UNION SELECT source FROM participant_presence_v2 WHERE meeting_id IN ($legacy, $normalized);";
                findSources.Parameters.AddWithValue("$legacy", legacyMeetingId);
                findSources.Parameters.AddWithValue("$normalized", normalizedMeetingId);
                await using var reader = await findSources.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    sources.Add(reader.GetString(0));
                }
            }

            foreach (var source in sources)
            {
                var legacyCapturedAt = await GetSnapshotCapturedAtAsync(
                    connection, transaction, legacyMeetingId, source, cancellationToken);
                var normalizedCapturedAt = await GetSnapshotCapturedAtAsync(
                    connection, transaction, normalizedMeetingId, source, cancellationToken);
                var keepLegacy = legacyCapturedAt is not null
                    && (normalizedCapturedAt is null || legacyCapturedAt > normalizedCapturedAt);

                if (keepLegacy)
                {
                    await DeleteMeetingSourceAsync(connection, transaction, normalizedMeetingId, source, cancellationToken);
                    await MoveMeetingSourceAsync(
                        connection, transaction, legacyMeetingId, normalizedMeetingId, source, cancellationToken);
                }
                else if (normalizedCapturedAt is not null)
                {
                    await DeleteMeetingSourceAsync(connection, transaction, legacyMeetingId, source, cancellationToken);
                }
                else
                {
                    // Legacy databases can have presence rows without a snapshot row.
                    await MoveMeetingSourceAsync(
                        connection, transaction, legacyMeetingId, normalizedMeetingId, source, cancellationToken);
                }
            }
        }

        await using (var markApplied = connection.CreateCommand())
        {
            markApplied.Transaction = transaction;
            markApplied.CommandText =
                "INSERT INTO schema_migrations (migration_id, applied_at) VALUES ('meeting-id-v1', $appliedAt);";
            markApplied.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
            await markApplied.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset?> GetSnapshotCapturedAtAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string meetingId,
        string source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT captured_at FROM participant_snapshots WHERE meeting_id = $meetingId AND source = $source;";
        command.Parameters.AddWithValue("$meetingId", meetingId);
        command.Parameters.AddWithValue("$source", source);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return value is null ? null : DateTimeOffset.Parse(value);
    }

    private static async Task DeleteMeetingSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string meetingId,
        string source,
        CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "participant_presence_v2", "participant_snapshots" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE meeting_id = $meetingId AND source = $source;";
            command.Parameters.AddWithValue("$meetingId", meetingId);
            command.Parameters.AddWithValue("$source", source);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task MoveMeetingSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string legacyMeetingId,
        string normalizedMeetingId,
        string source,
        CancellationToken cancellationToken)
    {
        await using (var movePresence = connection.CreateCommand())
        {
            movePresence.Transaction = transaction;
            movePresence.CommandText =
                "INSERT OR IGNORE INTO participant_presence_v2 " +
                "(meeting_id, source, presence_key, normalized_name, display_name, first_seen_at, last_seen_at, participant_email, raw_display_name, canonical_name) " +
                "SELECT $normalized, source, presence_key, normalized_name, display_name, first_seen_at, last_seen_at, participant_email, raw_display_name, canonical_name " +
                "FROM participant_presence_v2 WHERE meeting_id = $legacy AND source = $source; " +
                "DELETE FROM participant_presence_v2 WHERE meeting_id = $legacy AND source = $source;";
            movePresence.Parameters.AddWithValue("$legacy", legacyMeetingId);
            movePresence.Parameters.AddWithValue("$normalized", normalizedMeetingId);
            movePresence.Parameters.AddWithValue("$source", source);
            await movePresence.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var moveSnapshot = connection.CreateCommand())
        {
            moveSnapshot.Transaction = transaction;
            moveSnapshot.CommandText =
                "UPDATE participant_snapshots SET meeting_id = $normalized " +
                "WHERE meeting_id = $legacy AND source = $source;";
            moveSnapshot.Parameters.AddWithValue("$legacy", legacyMeetingId);
            moveSnapshot.Parameters.AddWithValue("$normalized", normalizedMeetingId);
            moveSnapshot.Parameters.AddWithValue("$source", source);
            await moveSnapshot.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ExecuteMigrationCommandAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string legacyMeetingId,
        string normalizedMeetingId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$legacy", legacyMeetingId);
        command.Parameters.AddWithValue("$normalized", normalizedMeetingId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async Task<List<StoredRosterIdentity>> ReadRosterIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var identities = new List<StoredRosterIdentity>();

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, normalized_name, email FROM roster_people;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            identities.Add(new StoredRosterIdentity(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2)));
        }

        return identities;
    }

    /// <summary>
    /// Builds the mapping from incoming roster people to already persisted roster person ids.
    /// Email matches are reserved first so row order cannot change identity ownership.
    /// </summary>
    public static RosterReconciliationResult ReconcileRosterIdentities(
        IReadOnlyList<StoredRosterIdentity> existingPeople,
        IReadOnlyList<RosterPerson> incomingPeople)
    {
        var existingByEmail = BuildUniqueIndex(
            existingPeople,
            identity => NormalizeEmailKey(identity.Email),
            identity => identity.Id);
        var existingByName = BuildUniqueIndex(
            existingPeople,
            identity => NormalizeNameKey(identity.NormalizedName),
            identity => identity.Id);

        var incomingEmailCounts = CountKeys(incomingPeople, person => NormalizeEmailKey(person.Email));
        var incomingNameCounts = CountKeys(incomingPeople, person => NormalizeNameKey(person.NormalizedName));

        var resolvedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var claimedExistingIds = new HashSet<string>(StringComparer.Ordinal);
        var preservedIds = new List<string>();
        var newIds = new List<string>();
        var existingIds = existingPeople.Select(person => person.Id).ToHashSet(StringComparer.Ordinal);
        var unavailableIds = new HashSet<string>(existingIds, StringComparer.Ordinal);
        var incomingIds = incomingPeople.Select(person => person.Id).ToHashSet(StringComparer.Ordinal);
        if (incomingIds.Count != incomingPeople.Count)
        {
            throw new ArgumentException("Incoming roster person ids must be unique.", nameof(incomingPeople));
        }
        unavailableIds.UnionWith(incomingIds);

        // A name-only match must never consume the identity of a later email match.
        foreach (var person in incomingPeople)
        {
            var emailKey = NormalizeEmailKey(person.Email);
            if (emailKey is not null
                && incomingEmailCounts[emailKey] == 1
                && existingByEmail.TryGetValue(emailKey, out var existingId))
            {
                resolvedIds[person.Id] = existingId;
                claimedExistingIds.Add(existingId);
            }
        }

        foreach (var person in incomingPeople)
        {
            if (resolvedIds.TryGetValue(person.Id, out var emailMatchId))
            {
                preservedIds.Add(emailMatchId);
                continue;
            }

            var nameKey = NormalizeNameKey(person.NormalizedName);
            if (nameKey is not null
                && incomingNameCounts[nameKey] == 1
                && existingByName.TryGetValue(nameKey, out var nameMatchId)
                && claimedExistingIds.Add(nameMatchId))
            {
                resolvedIds[person.Id] = nameMatchId;
                preservedIds.Add(nameMatchId);
                continue;
            }

            var newId = person.Id;
            if (existingIds.Contains(newId))
            {
                // Excel ids depend on sequence/name, which can be reused by a different person.
                do { newId = Guid.NewGuid().ToString("N"); }
                while (!unavailableIds.Add(newId));
            }
            resolvedIds[person.Id] = newId;
            newIds.Add(newId);
        }

        return new RosterReconciliationResult(resolvedIds, preservedIds, newIds);
    }

    /// <summary>
    /// Indexes source items by key, dropping any key that occurs more than once so ambiguous
    /// identities can never be resolved to a single stored person.
    /// </summary>
    private static Dictionary<string, string> BuildUniqueIndex<T>(
        IReadOnlyList<T> source,
        Func<T, string?> keySelector,
        Func<T, string> valueSelector)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in source)
        {
            var key = keySelector(item);
            if (key is null)
            {
                continue;
            }

            if (index.ContainsKey(key))
            {
                ambiguousKeys.Add(key);
                continue;
            }

            index[key] = valueSelector(item);
        }

        foreach (var ambiguousKey in ambiguousKeys)
        {
            index.Remove(ambiguousKey);
        }

        return index;
    }

    private static Dictionary<string, int> CountKeys<T>(IReadOnlyList<T> source, Func<T, string?> keySelector)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var item in source)
        {
            var key = keySelector(item);
            if (key is null)
            {
                continue;
            }

            counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        return counts;
    }

    private static string? NormalizeEmailKey(string? email)
    {
        var trimmed = email?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToLowerInvariant();
    }

    private static string? NormalizeNameKey(string? normalizedName)
    {
        var trimmed = normalizedName?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// Minimal projection of a persisted roster person used for identity reconciliation.
/// </summary>
public sealed record StoredRosterIdentity(string Id, string NormalizedName, string Email);

/// <summary>
/// Outcome of reconciling an incoming roster against the stored roster.
/// </summary>
public sealed class RosterReconciliationResult
{
    private readonly IReadOnlyDictionary<string, string> _resolvedIdsByIncomingId;

    internal RosterReconciliationResult(
        IReadOnlyDictionary<string, string> resolvedIdsByIncomingId,
        IReadOnlyList<string> preservedRosterPersonIds,
        IReadOnlyList<string> newRosterPersonIds)
    {
        _resolvedIdsByIncomingId = resolvedIdsByIncomingId;
        PreservedRosterPersonIds = preservedRosterPersonIds;
        NewRosterPersonIds = newRosterPersonIds;
    }

    /// <summary>Ids of stored people whose identity was carried over to the new import.</summary>
    public IReadOnlyList<string> PreservedRosterPersonIds { get; }

    /// <summary>Ids of people persisted as new, including deliberately unresolved ambiguous ones.</summary>
    public IReadOnlyList<string> NewRosterPersonIds { get; }

    /// <summary>Resolves the id to persist for an incoming roster person.</summary>
    public string ResolveId(RosterPerson person)
        => _resolvedIdsByIncomingId.TryGetValue(person.Id, out var resolved) ? resolved : person.Id;
}
