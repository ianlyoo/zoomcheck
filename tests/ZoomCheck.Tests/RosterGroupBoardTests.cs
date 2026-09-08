using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using ZoomCheck.Backend.Controllers;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

/// <summary>
/// Covers how roster groups surface on the attendance board, in the CSV export, and through the
/// export endpoint's optional group filter.
/// </summary>
public sealed class RosterGroupBoardTests : IAsyncLifetime
{
    private const string MeetingId = "123456789";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"zoomcheck-groups-{Guid.NewGuid():N}.db");

    private SqliteAttendanceRepository _repository = null!;
    private AttendanceApplicationService _service = null!;
    private MeetingsController _controller = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteAttendanceRepository(_databasePath);
        await _repository.InitializeAsync();
        _service = new AttendanceApplicationService(
            new ExcelRosterParser(),
            _repository,
            new AttendanceMatcher());
        _controller = new MeetingsController(_service);

        // Groups are deliberately out of alphabetical order and repeated so roster order and
        // de-duplication are both observable.
        await _repository.ReplaceRosterAsync(new RosterImportResult(
            ImportId: "import-1",
            SourcePath: "roster.xlsx",
            DisplayName: "roster.xlsx",
            ImportedAt: DateTimeOffset.UtcNow,
            People: new[]
            {
                Person("p1", "1", "김영인", "2조"),
                Person("p2", "2", "이순신", "1조"),
                Person("p3", "3", "홍길동", "2조"),
                Person("p4", "4", "강감찬", "")
            }));
    }

    public Task DisposeAsync()
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

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Board_CarriesGroupPerPerson_AndDistinctGroupsInRosterOrder()
    {
        var board = await _service.BuildBoardAsync(MeetingId);

        Assert.Equal("2조", board.People.Single(person => person.RosterPersonId == "p1").Group);
        Assert.Equal("1조", board.People.Single(person => person.RosterPersonId == "p2").Group);
        Assert.Equal(string.Empty, board.People.Single(person => person.RosterPersonId == "p4").Group);

        // Roster order, de-duplicated, ungrouped people excluded.
        Assert.Equal(new[] { "2조", "1조" }, board.Groups);
    }

    [Fact]
    public async Task Board_ReportsNoGroups_WhenRosterIsUngrouped()
    {
        await _repository.ReplaceRosterAsync(new RosterImportResult(
            ImportId: "import-2",
            SourcePath: "roster.xlsx",
            DisplayName: "roster.xlsx",
            ImportedAt: DateTimeOffset.UtcNow,
            People: new[] { Person("p1", "1", "김영인", "") }));

        var board = await _service.BuildBoardAsync(MeetingId);

        Assert.Empty(board.Groups!);
    }

    [Fact]
    public async Task Csv_IncludesGroupColumnForEveryPerson()
    {
        var csv = await _service.BuildBoardCsvAsync(MeetingId);
        var lines = SplitLines(csv);

        Assert.Equal(
            "Sequence,Name,Organization,Group,AttendanceState,Confidence,ConfidenceReason,LastJoinedAt,LastLeftAt,JoinCount,IdentityReviewStatus,DuplicateReviewStatus",
            lines[0]);
        Assert.Equal(5, lines.Length);
        Assert.Contains("\"김영인\",\"\",\"2조\"", lines[1]);
        Assert.Contains("\"강감찬\",\"\",\"\"", lines[4]);
    }

    [Fact]
    public async Task Csv_FiltersToRequestedGroup()
    {
        var csv = await _service.BuildBoardCsvAsync(MeetingId, "2조");
        var lines = SplitLines(csv);

        Assert.Equal(3, lines.Length);
        Assert.Contains("김영인", lines[1]);
        Assert.Contains("홍길동", lines[2]);
    }

    [Theory]
    [InlineData(" 2조 ")]
    [InlineData("2조")]
    public async Task Csv_GroupFilterIgnoresSurroundingWhitespace(string filter)
    {
        var lines = SplitLines(await _service.BuildBoardCsvAsync(MeetingId, filter));

        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task Csv_GroupFilterCollapsesInternalWhitespace()
    {
        await _repository.ReplaceRosterAsync(new RosterImportResult(
            ImportId: "import-3",
            SourcePath: "roster.xlsx",
            DisplayName: "roster.xlsx",
            ImportedAt: DateTimeOffset.UtcNow,
            People: new[] { Person("p1", "1", "김영인", "1 조") }));

        var lines = SplitLines(await _service.BuildBoardCsvAsync(MeetingId, "1    조"));

        Assert.Equal(2, lines.Length);
        Assert.Contains("김영인", lines[1]);
    }

    [Fact]
    public async Task Csv_BlankGroupFilterExportsEveryone()
    {
        foreach (var filter in new[] { null, string.Empty, "   " })
        {
            Assert.Equal(5, SplitLines(await _service.BuildBoardCsvAsync(MeetingId, filter)).Length);
        }
    }

    [Fact]
    public async Task Csv_UnknownGroupExportsHeaderOnly()
    {
        var lines = SplitLines(await _service.BuildBoardCsvAsync(MeetingId, "없는조"));

        Assert.Single(lines);
    }

    [Fact]
    public async Task Export_ReturnsFilteredCsv_ForGroupQuery()
    {
        var response = await _controller.ExportCsv(MeetingId, "1조", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(response);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.FileContents.Take(3).ToArray());
        var lines = SplitLines(System.Text.Encoding.UTF8.GetString(file.FileContents));
        Assert.Equal(2, lines.Length);
        Assert.Contains("이순신", lines[1]);
    }

    [Fact]
    public async Task Export_RejectsOverlongGroupQuery()
    {
        var response = await _controller.ExportCsv(MeetingId, new string('조', 101), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    [Fact]
    public async Task Export_AcceptsGroupQueryAtMaximumLength()
    {
        var response = await _controller.ExportCsv(MeetingId, new string('조', 100), CancellationToken.None);

        Assert.IsType<FileContentResult>(response);
    }

    [Fact]
    public async Task Groups_DoNotAffectMatchingOrAttendance()
    {
        var result = await _service.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
            MeetingId,
            new[] { "김영인" },
            "manual-snapshot",
            DateTimeOffset.UtcNow));

        var matched = result.Board.People.Single(person => person.RosterPersonId == "p1");
        Assert.Equal(AttendanceState.Present, matched.AttendanceState);
        Assert.Equal("2조", matched.Group);
        Assert.Equal(
            AttendanceState.NotJoined,
            result.Board.People.Single(person => person.RosterPersonId == "p3").AttendanceState);
    }

    private static string[] SplitLines(string csv)
        => csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();

    private static RosterPerson Person(string id, string sequence, string name, string group)
        => new(
            Id: id,
            Sequence: sequence,
            Name: name,
            NormalizedName: NameNormalizer.Normalize(name),
            Email: string.Empty,
            Phone: string.Empty,
            Organization: string.Empty,
            Aliases: new[] { NameNormalizer.Normalize(name) },
            Group: group);
}
