using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

public sealed class ParticipantSnapshotTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"zoomcheck-snapshot-{Guid.NewGuid():N}.db");

    private SqliteAttendanceRepository _repository = null!;
    private AttendanceApplicationService _service = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteAttendanceRepository(_databasePath);
        await _repository.InitializeAsync();
        _service = new AttendanceApplicationService(
            new ExcelRosterParser(),
            _repository,
            new AttendanceMatcher());

        await _repository.ReplaceRosterAsync(new RosterImportResult(
            ImportId: "test-roster",
            SourcePath: "test.xlsx",
            DisplayName: "test.xlsx",
            ImportedAt: DateTimeOffset.UtcNow,
            People: new[]
            {
                Person("p1", "김영인", "1"),
                Person("p2", "이순신", "2")
            }));
    }

    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task ApplySnapshot_DerivesJoinsLeavesAndIdempotentResubmission()
    {
        var first = await Apply("meeting-1", "manual-snapshot", " 김 영 인 ", "이순신");

        Assert.Equal(2, first.PresentCount);
        Assert.Equal(2, first.JoinedNames.Count);
        Assert.Empty(first.LeftNames);
        Assert.All(first.Board.People, person => Assert.Equal(AttendanceState.Present, person.AttendanceState));

        var repeated = await Apply("meeting-1", "manual-snapshot", "김영인", "이순신");

        Assert.Empty(repeated.JoinedNames);
        Assert.Empty(repeated.LeftNames);
        Assert.Equal(2, repeated.Board.RecentEvents.Count);

        var changed = await Apply("meeting-1", "manual-snapshot", "이순신");

        Assert.Empty(changed.JoinedNames);
        Assert.Equal(new[] { "김영인" }, changed.LeftNames);
        Assert.Equal(AttendanceState.Left, changed.Board.People.Single(person => person.RosterPersonId == "p1").AttendanceState);
        Assert.Equal(AttendanceState.Present, changed.Board.People.Single(person => person.RosterPersonId == "p2").AttendanceState);
    }

    [Fact]
    public async Task ApplySnapshot_DeduplicatesNormalizedNamesAndIgnoresPunctuationOnlyRows()
    {
        var result = await Apply(
            "meeting-1",
            "manual-snapshot",
            "김영인",
            " 김 영 인 ",
            "---",
            "   ");

        Assert.Equal(1, result.PresentCount);
        Assert.Single(result.JoinedNames);
        Assert.Equal(new[] { "---" }, result.IgnoredNames);
    }

    [Fact]
    public async Task ApplySnapshot_IsolatesPresenceBySource()
    {
        await Apply("meeting-1", "manual-snapshot", "김영인");
        await Apply("meeting-1", "panel-uia", "이순신");

        var manualCleared = await Apply("meeting-1", "manual-snapshot");

        Assert.Equal(new[] { "김영인" }, manualCleared.LeftNames);
        Assert.Equal(AttendanceState.Left, manualCleared.Board.People.Single(person => person.RosterPersonId == "p1").AttendanceState);
        Assert.Equal(AttendanceState.Present, manualCleared.Board.People.Single(person => person.RosterPersonId == "p2").AttendanceState);
    }

    [Fact]
    public async Task ApplySnapshot_IsolatesPresenceByMeeting()
    {
        await Apply("meeting-1", "manual-snapshot", "김영인");
        await Apply("meeting-2", "manual-snapshot", "이순신");

        await Apply("meeting-1", "manual-snapshot");
        var secondBoard = await _service.BuildBoardAsync("meeting-2");

        Assert.Equal(AttendanceState.Present, secondBoard.People.Single(person => person.RosterPersonId == "p2").AttendanceState);
        Assert.Equal(AttendanceState.NotJoined, secondBoard.People.Single(person => person.RosterPersonId == "p1").AttendanceState);
    }

    private Task<ParticipantSnapshotResult> Apply(string meetingId, string source, params string[] names)
        => _service.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
            meetingId,
            names,
            source,
            DateTimeOffset.UtcNow));

    private static RosterPerson Person(string id, string name, string sequence)
        => new(
            Id: id,
            Sequence: sequence,
            Name: name,
            NormalizedName: NameNormalizer.Normalize(name),
            Email: string.Empty,
            Phone: string.Empty,
            Organization: string.Empty,
            Aliases: new[] { NameNormalizer.Normalize(name) });
}
