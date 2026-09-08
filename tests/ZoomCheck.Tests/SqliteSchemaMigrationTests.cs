using Microsoft.Data.Sqlite;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Infrastructure.Persistence;

namespace ZoomCheck.Tests;

public sealed class SqliteSchemaMigrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"zoomcheck-migration-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Initialize_UpgradesLegacyDatabase_AndPreservesExistingRows()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        // Build the pre-name-change schema, including the original participant_presence table
        // so the presence-v2 migration also runs.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE participant_events (
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
                CREATE TABLE participant_presence (
                    meeting_id TEXT NOT NULL,
                    source TEXT NOT NULL,
                    normalized_name TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    first_seen_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    PRIMARY KEY (meeting_id, source, normalized_name)
                );
                CREATE TABLE participant_snapshots (
                    meeting_id TEXT NOT NULL,
                    source TEXT NOT NULL,
                    captured_at TEXT NOT NULL,
                    present_count INTEGER NOT NULL,
                    PRIMARY KEY (meeting_id, source)
                );
                INSERT INTO participant_events VALUES ('e1','123 456-789','2024-01-01T00:00:00.0000000+00:00','Joined','이순신','이순신',NULL,'NameOnly','p2','manual-snapshot','{}');
                INSERT INTO participant_presence VALUES ('123 456-789','manual-snapshot','이순신','이순신','2024-01-01T00:00:00.0000000+00:00','2024-01-01T00:00:00.0000000+00:00');
                INSERT INTO participant_snapshots VALUES ('123 456-789','manual-snapshot','2024-01-01T00:00:00.0000000+00:00',1);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAttendanceRepository(_databasePath);
        await repository.InitializeAsync();

        var events = await repository.GetParticipantEventsAsync("123456789");
        var legacyEvent = Assert.Single(events);
        Assert.Equal(ParticipantEventType.Joined, legacyEvent.EventType);
        Assert.Equal("이순신", legacyEvent.ParticipantName);
        // New columns read back as null and fall back to the existing name.
        Assert.Null(legacyEvent.PresenceKey);
        Assert.Null(legacyEvent.RawParticipantName);
        Assert.Null(legacyEvent.CanonicalParticipantName);
        Assert.Equal("이순신", legacyEvent.EffectiveRawParticipantName);

        var presence = await repository.GetParticipantPresenceAsync("123456789", "manual-snapshot");
        var entry = Assert.Single(presence);
        Assert.Equal("이순신", entry.DisplayName);
        Assert.Equal("이순신", entry.PresenceKey);
        Assert.Null(entry.RawDisplayName);
        Assert.Null(entry.CanonicalName);
        Assert.Equal("이순신", entry.EffectiveRawDisplayName);

        var snapshots = await repository.GetParticipantSnapshotSourcesAsync("123456789");
        Assert.Equal(1, Assert.Single(snapshots).PresentCount);

        // Re-initializing an already-upgraded database must stay a no-op.
        await repository.InitializeAsync();
        Assert.Single(await repository.GetParticipantEventsAsync("123456789"));
    }

    [Fact]
    public async Task Initialize_AddsRosterGroupColumn_AndKeepsExistingRosterRows()
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        // Pre-group roster schema with a person already imported.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE roster_people (
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
                INSERT INTO roster_people VALUES ('p1','import-1','1','이순신','이순신','sunshin@example.com','','해군','["이순신"]');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteAttendanceRepository(_databasePath);
        await repository.InitializeAsync();

        var legacyPerson = Assert.Single(await repository.GetRosterPeopleAsync());
        Assert.Equal("이순신", legacyPerson.Name);
        Assert.Equal("해군", legacyPerson.Organization);
        // Additive column: pre-existing rows read back as ungrouped rather than failing.
        Assert.Equal(string.Empty, legacyPerson.Group);

        var columns = await GetColumnsAsync(connectionString, "roster_people");
        Assert.Contains("group_name", columns);

        // Re-initializing must not add the column twice or disturb the row.
        await repository.InitializeAsync();
        Assert.Equal(string.Empty, Assert.Single(await repository.GetRosterPeopleAsync()).Group);
    }

    [Fact]
    public async Task ReplaceRoster_RoundTripsGroup_OnAFreshDatabase()
    {
        var repository = new SqliteAttendanceRepository(_databasePath);
        await repository.InitializeAsync();

        await repository.ReplaceRosterAsync(new RosterImportResult(
            ImportId: "import-1",
            SourcePath: "roster.xlsx",
            DisplayName: "roster.xlsx",
            ImportedAt: DateTimeOffset.UtcNow,
            People: new[]
            {
                new RosterPerson("p1", "1", "김영인", "김영인", "", "", "", Array.Empty<string>(), "1조"),
                new RosterPerson("p2", "2", "이순신", "이순신", "", "", "", Array.Empty<string>())
            }));

        var people = await repository.GetRosterPeopleAsync();

        Assert.Equal("1조", people.Single(person => person.Id == "p1").Group);
        Assert.Equal(string.Empty, people.Single(person => person.Id == "p2").Group);
    }

    private static async Task<IReadOnlyList<string>> GetColumnsAsync(string connectionString, string tableName)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
