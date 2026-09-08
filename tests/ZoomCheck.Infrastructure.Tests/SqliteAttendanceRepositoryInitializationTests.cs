using Microsoft.Data.Sqlite;
using Xunit;

namespace ZoomCheck.Infrastructure.Tests;

public sealed class SqliteAttendanceRepositoryInitializationTests
{
    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndKeepsExistingRows()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        var roster = RosterFactory.Import(RosterFactory.Person("김민수", email: "minsu@example.com"));
        await repository.ReplaceRosterAsync(roster);

        // Re-running initialization must be safe on an already populated database.
        await repository.InitializeAsync();
        await repository.InitializeAsync();

        var people = await repository.GetRosterPeopleAsync();
        Assert.Single(people);
        Assert.Equal("김민수", people[0].Name);
    }

    [Fact]
    public async Task InitializeAsync_CreatesExpectedTables()
    {
        using var database = TempSqliteDatabase.Create();
        await database.CreateInitializedRepositoryAsync();

        var tables = await ReadTableNamesAsync(database.DatabasePath);

        Assert.Contains("roster_imports", tables);
        Assert.Contains("roster_people", tables);
        Assert.Contains("manual_aliases", tables);
        Assert.Contains("participant_events", tables);
    }

    [Fact]
    public async Task InitializeAsync_OnSeparateRepositoryInstance_DoesNotDropData()
    {
        using var database = TempSqliteDatabase.Create();
        var first = await database.CreateInitializedRepositoryAsync();
        await first.ReplaceRosterAsync(RosterFactory.Import(RosterFactory.Person("이영희", email: "younghee@example.com")));

        // Simulates a backend restart against the same database file.
        var second = await database.CreateInitializedRepositoryAsync();
        var people = await second.GetRosterPeopleAsync();

        Assert.Single(people);
        Assert.Equal("younghee@example.com", people[0].Email);
    }

    private static async Task<List<string>> ReadTableNamesAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
