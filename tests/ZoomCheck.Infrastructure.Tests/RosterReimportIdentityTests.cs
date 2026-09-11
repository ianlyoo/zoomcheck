using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using Xunit;

namespace ZoomCheck.Infrastructure.Tests;

/// <summary>
/// Integration coverage for roster re-import identity preservation: re-importing the same roster
/// must not orphan participant events or manual aliases that point at retained people.
/// </summary>
public sealed class RosterReimportIdentityTests
{
    [Fact]
    public async Task Reimport_MatchedByEmail_PreservesPersistedIdAndEventLinkage()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "minsu@example.com")));

        var storedId = (await repository.GetRosterPeopleAsync()).Single().Id;
        await repository.AppendParticipantEventAsync(BuildEvent("김민수", storedId));

        // Second import: same person, brand-new incoming id, name changed but email stable.
        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수(재등록)", sequence: "1", email: "MINSU@example.com")));

        var reimported = Assert.Single(await repository.GetRosterPeopleAsync());
        Assert.Equal(storedId, reimported.Id);

        var events = await repository.GetParticipantEventsAsync("meeting-1");
        Assert.Equal(storedId, Assert.Single(events).MatchedRosterPersonId);
    }

    [Fact]
    public async Task Reimport_MatchedByNormalizedName_PreservesManualAliasLinkage()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("이영희", sequence: "2")));

        var storedId = (await repository.GetRosterPeopleAsync()).Single().Id;
        await repository.UpsertAliasAsync(NameNormalizer.Normalize("영희 iPhone"), storedId, "operator confirmed");

        // No email on either side, so reconciliation must fall back to normalized name.
        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("이영희", sequence: "7", organization: "2반")));

        var reimported = Assert.Single(await repository.GetRosterPeopleAsync());
        Assert.Equal(storedId, reimported.Id);

        var aliases = await repository.GetAliasMapAsync();
        Assert.Equal(storedId, aliases[NameNormalizer.Normalize("영희 iPhone")]);
    }

    [Fact]
    public async Task Reimport_UpdatesCurrentFieldsAndImportIdForRetainedPerson()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("박지훈", sequence: "3", email: "jihun@example.com", phone: "01011112222", organization: "1반")));

        var storedId = (await repository.GetRosterPeopleAsync()).Single().Id;

        var secondImport = RosterFactory.Import(
            RosterFactory.Person("박지훈", sequence: "9", email: "jihun@example.com", phone: "01033334444", organization: "3반"));
        await repository.ReplaceRosterAsync(secondImport);

        var reimported = Assert.Single(await repository.GetRosterPeopleAsync());
        Assert.Equal(storedId, reimported.Id);
        Assert.Equal("9", reimported.Sequence);
        Assert.Equal("01033334444", reimported.Phone);
        Assert.Equal("3반", reimported.Organization);
        Assert.Equal(secondImport.ImportId, await ReadImportIdAsync(database.DatabasePath, storedId));
    }

    [Fact]
    public async Task Reimport_RemovedPerson_IsDroppedWhileRetainedPersonKeepsId()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "minsu@example.com"),
            RosterFactory.Person("이영희", sequence: "2", email: "younghee@example.com")));

        var storedPeople = await repository.GetRosterPeopleAsync();
        var retainedId = storedPeople.Single(person => person.Email == "minsu@example.com").Id;

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "minsu@example.com"),
            RosterFactory.Person("최수진", sequence: "2", email: "sujin@example.com")));

        var afterReimport = await repository.GetRosterPeopleAsync();
        Assert.Equal(2, afterReimport.Count);
        Assert.Equal(retainedId, afterReimport.Single(person => person.Email == "minsu@example.com").Id);
        Assert.DoesNotContain(afterReimport, person => person.Email == "younghee@example.com");
    }

    [Fact]
    public async Task Reimport_NewPerson_KeepsIncomingIdAndIsReportedAsNew()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "minsu@example.com")));

        var newcomer = RosterFactory.Person("최수진", sequence: "2", email: "sujin@example.com");
        var result = await repository.ReplaceRosterWithReconciliationAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "minsu@example.com"),
            newcomer));

        Assert.Single(result.NewRosterPersonIds);
        Assert.Contains(newcomer.Id, result.NewRosterPersonIds);
        Assert.Single(result.PreservedRosterPersonIds);

        var people = await repository.GetRosterPeopleAsync();
        Assert.Contains(people, person => person.Id == newcomer.Id);
    }

    [Fact]
    public async Task Reimport_AmbiguousDuplicateNames_AreNotGuessed()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        // Two people share a normalized name and neither has an email, so no key is unique.
        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1"),
            RosterFactory.Person("김민수", sequence: "2")));

        var storedIds = (await repository.GetRosterPeopleAsync()).Select(person => person.Id).ToHashSet();

        var secondImport = RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1"),
            RosterFactory.Person("김민수", sequence: "2"));
        var result = await repository.ReplaceRosterWithReconciliationAsync(secondImport);

        Assert.Empty(result.PreservedRosterPersonIds);
        Assert.Equal(2, result.NewRosterPersonIds.Count);

        var afterReimport = await repository.GetRosterPeopleAsync();
        Assert.Equal(2, afterReimport.Count);
        Assert.All(afterReimport, person => Assert.DoesNotContain(person.Id, storedIds));
    }

    [Fact]
    public async Task Reimport_AmbiguousIncomingEmail_FallsBackWithoutCrossLinking()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "shared@example.com")));

        var storedId = (await repository.GetRosterPeopleAsync()).Single().Id;

        // Shared mailbox on the incoming side makes the email key ambiguous. Only the person whose
        // normalized name still uniquely matches may inherit the stored id.
        var result = await repository.ReplaceRosterWithReconciliationAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "shared@example.com"),
            RosterFactory.Person("최수진", sequence: "2", email: "shared@example.com")));

        Assert.Equal(new[] { storedId }, result.PreservedRosterPersonIds);

        var people = await repository.GetRosterPeopleAsync();
        Assert.Equal(storedId, people.Single(person => person.Name == "김민수").Id);
        Assert.NotEqual(storedId, people.Single(person => person.Name == "최수진").Id);
    }

    [Fact]
    public async Task Reimport_DoesNotReuseOneStoredIdForTwoIncomingPeople()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "minsu@example.com")));

        var storedId = (await repository.GetRosterPeopleAsync()).Single().Id;

        // Same normalized name as the stored person, but a different (unique) email each.
        var result = await repository.ReplaceRosterWithReconciliationAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "minsu@example.com"),
            RosterFactory.Person("김민수", sequence: "2", email: "minsu.kim@example.com")));

        Assert.Equal(new[] { storedId }, result.PreservedRosterPersonIds);

        var people = await repository.GetRosterPeopleAsync();
        Assert.Equal(2, people.Count);
        Assert.Equal(2, people.Select(person => person.Id).Distinct().Count());
    }

    [Fact]
    public void ReconcileRosterIdentities_PrefersEmailOverNormalizedName()
    {
        var stored = new[]
        {
            new StoredRosterIdentity("stored-email", NameNormalizer.Normalize("옛이름"), "minsu@example.com"),
            new StoredRosterIdentity("stored-name", NameNormalizer.Normalize("김민수"), "other@example.com")
        };

        var incoming = RosterFactory.Person("김민수", email: "minsu@example.com");
        var result = SqliteAttendanceRepository.ReconcileRosterIdentities(stored, new[] { incoming });

        Assert.Equal("stored-email", result.ResolveId(incoming));
    }

    [Fact]
    public async Task Reimport_WithFailureMidway_LeavesPreviousRosterIntact()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();

        await repository.ReplaceRosterAsync(RosterFactory.Import(
            RosterFactory.Person("김민수", sequence: "1", email: "minsu@example.com")));

        var storedId = (await repository.GetRosterPeopleAsync()).Single().Id;

        // Two incoming people carrying the same primary key forces a mid-transaction failure.
        var duplicateId = Guid.NewGuid().ToString("N");
        var brokenImport = RosterFactory.Import(
            RosterFactory.Person("최수진", sequence: "1", email: "sujin@example.com", id: duplicateId),
            RosterFactory.Person("정하윤", sequence: "2", email: "hayun@example.com", id: duplicateId));

        await Assert.ThrowsAnyAsync<Exception>(() => repository.ReplaceRosterAsync(brokenImport));

        var people = await repository.GetRosterPeopleAsync();
        var survivor = Assert.Single(people);
        Assert.Equal(storedId, survivor.Id);
        Assert.Equal("minsu@example.com", survivor.Email);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconcileRosterIdentities_ReservesEmailMatchesBeforeNameFallback(bool reverseOrder)
    {
        var stored = new[]
        {
            new StoredRosterIdentity("retained", NameNormalizer.Normalize("김민수"), "minsu@example.com")
        };
        var newcomer = RosterFactory.Person("김민수", email: "new@example.com");
        var renamed = RosterFactory.Person("김수민", email: "minsu@example.com");
        var incoming = reverseOrder ? new[] { renamed, newcomer } : new[] { newcomer, renamed };

        var result = SqliteAttendanceRepository.ReconcileRosterIdentities(stored, incoming);

        Assert.Equal("retained", result.ResolveId(renamed));
        Assert.Equal(newcomer.Id, result.ResolveId(newcomer));
    }

    [Fact]
    public void ReconcileRosterIdentities_DoesNotGiveNewPersonAnAlreadyPreservedId()
    {
        var stored = new[]
        {
            new StoredRosterIdentity("retained", NameNormalizer.Normalize("김민수"), "minsu@example.com")
        };
        // The Excel parser derives ids from sequence/name; a replacement can receive the same id.
        var newcomer = RosterFactory.Person("김민수", email: "new@example.com", id: "retained");
        var renamed = RosterFactory.Person("김수민", email: "minsu@example.com");

        var result = SqliteAttendanceRepository.ReconcileRosterIdentities(stored, new[] { newcomer, renamed });

        Assert.Equal("retained", result.ResolveId(renamed));
        Assert.NotEqual("retained", result.ResolveId(newcomer));
        Assert.Contains(result.ResolveId(newcomer), result.NewRosterPersonIds);
    }

    [Fact]
    public async Task Reimport_PreservesIdentityAndUpdatesGroup()
    {
        using var database = TempSqliteDatabase.Create();
        var repository = await database.CreateInitializedRepositoryAsync();
        var original = RosterFactory.Person("김민수", email: "minsu@example.com") with { Group = "1조" };
        await repository.ReplaceRosterAsync(RosterFactory.Import(original));

        var incoming = RosterFactory.Person("김민수", email: "minsu@example.com") with { Group = "2조" };
        await repository.ReplaceRosterAsync(RosterFactory.Import(incoming));

        var persisted = Assert.Single(await repository.GetRosterPeopleAsync());
        Assert.Equal(original.Id, persisted.Id);
        Assert.Equal("2조", persisted.Group);
    }

    private static ParticipantEvent BuildEvent(string participantName, string matchedRosterPersonId)
        => new(
            Id: Guid.NewGuid().ToString("N"),
            MeetingId: "meeting-1",
            OccurredAt: DateTimeOffset.UtcNow,
            EventType: ParticipantEventType.Joined,
            ParticipantName: participantName,
            NormalizedParticipantName: NameNormalizer.Normalize(participantName),
            ParticipantEmail: null,
            Confidence: MatchConfidence.Verified,
            MatchedRosterPersonId: matchedRosterPersonId,
            Source: "test",
            RawPayload: "{}");

    private static async Task<string> ReadImportIdAsync(string databasePath, string rosterPersonId)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT import_id FROM roster_people WHERE id = $id;";
        command.Parameters.AddWithValue("$id", rosterPersonId);

        return (string)(await command.ExecuteScalarAsync())!;
    }
}
