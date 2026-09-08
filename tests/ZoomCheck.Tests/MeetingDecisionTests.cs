using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Controllers;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

public sealed class MeetingDecisionTests : IAsyncLifetime
{
    private const string Meeting = "1234567890";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"zoomcheck-decisions-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _start = new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    private SqliteAttendanceRepository _repository = null!;
    private AttendanceApplicationService _service = null!;
    private int _tick;

    public async Task InitializeAsync()
    {
        _repository = new(_path);
        await _repository.InitializeAsync();
        _service = CreateService(_repository);
        await _repository.ReplaceRosterAsync(Roster());
    }

    public Task DisposeAsync()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) { if (File.Exists(_path + suffix)) { File.Delete(_path + suffix); } }
        return Task.CompletedTask;
    }

    private static AttendanceApplicationService CreateService(SqliteAttendanceRepository repository) => new(new ExcelRosterParser(), repository, new AttendanceMatcher());
    private RosterImportResult Roster() => new("test", "test.xlsx", "test.xlsx", _start,
        new[] { Person("p1", "1", "김민수", "minsu@example.com", "1조"), Person("p2", "2", "이영희", "younghee@example.com", "1조"), Person("p3", "3", "박지훈", "jihun@example.com", "2조") });
    private static RosterPerson Person(string id, string sequence, string name, string email, string group)
        => new(id, sequence, name, NameNormalizer.Normalize(name), email, "", "", Array.Empty<string>(), group);
    private Task<ParticipantSnapshotResult> Snapshot(string source = "web-dashboard", string meeting = Meeting, params ParticipantSnapshotParticipant[] people)
        => _service.ApplyParticipantSnapshotAsync(new(meeting, people.Select(p => p.DisplayName).ToArray(), source, _start.AddSeconds(++_tick * 10), Participants: people));
    private static BoardPersonStatus P(AttendanceBoard board, string id = "p1") => board.People.Single(p => p.RosterPersonId == id);
    private static CurrentParticipantConnection C(AttendanceBoard board, string key = "a", string source = "web-dashboard")
        => board.CurrentConnections!.Single(c => c.PresenceKey == key && c.Source == source);

    [Fact]
    public async Task Exclusion_SurvivesRestartAndSync_WithoutDeletingRosterOrEvents()
    {
        var initial = await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") });
        var excluded = await _service.SetMeetingExclusionAsync("123 456 7890", "p1", true);
        Assert.True(P(excluded).IsExcluded);
        Assert.False(P(excluded).ReviewRequired);
        Assert.Equal(AttendanceState.Present, P(excluded).AttendanceState);
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") });
        var reopenedRepository = new SqliteAttendanceRepository(_path);
        await reopenedRepository.InitializeAsync();
        var reloaded = await CreateService(reopenedRepository).BuildBoardAsync(Meeting);
        Assert.True(P(reloaded).IsExcluded);
        Assert.Equal(initial.Board.RecentEvents.Count, reloaded.RecentEvents.Count);
        Assert.Equal(3, (await _service.GetRosterAsync()).Count);
        Assert.Single(reloaded.CurrentConnections!);
        Assert.Empty(reloaded.ConfidenceCounts);
        Assert.DoesNotContain("김민수", await _service.BuildBoardCsvAsync(Meeting));
    }

    [Fact]
    public async Task Restore_IsIdempotentAndPreservesOriginalAttendance()
    {
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") });
        await _service.SetMeetingExclusionAsync(Meeting, "p1", true);
        await _service.SetMeetingExclusionAsync(Meeting, "p1", true);
        await _service.SetMeetingExclusionAsync(Meeting, "p1", false);
        var restored = await _service.SetMeetingExclusionAsync(Meeting, "p1", false);
        Assert.False(P(restored).IsExcluded);
        Assert.Equal(AttendanceState.Present, P(restored).AttendanceState);
        Assert.Equal(1, P(restored).JoinCount);
        Assert.True(P(restored).ReviewRequired);
        Assert.Contains("김민수", await _service.BuildBoardCsvAsync(Meeting, "1조"));
    }

    [Fact]
    public async Task Exclusion_IsMeetingScoped_AndSurvivesSameRosterReimport()
    {
        await _service.SetMeetingExclusionAsync(Meeting, "p1", true);
        await _repository.ReplaceRosterAsync(Roster() with { ImportId = "again" });
        Assert.True(P(await _service.BuildBoardAsync(Meeting)).IsExcluded);
        Assert.False(P(await _service.BuildBoardAsync("another-meeting")).IsExcluded);
    }

    [Fact]
    public async Task NextLocalDay_RecurringMeetingIncludesPreviouslyExcludedPeople()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 5, 14, 59, 0, TimeSpan.Zero));
        var service = new AttendanceApplicationService(new ExcelRosterParser(), _repository, new AttendanceMatcher(), clock);
        var today = await service.SetMeetingExclusionAsync(Meeting, "p1", true);
        Assert.Equal("2026-09-05", today.AttendanceDate);
        Assert.True(P(today).IsExcluded);
        clock.Now = clock.Now.AddMinutes(2); // Midnight in the PC's local time zone, still Sep 5 UTC.
        var tomorrow = await service.BuildBoardAsync(Meeting);
        Assert.Equal("2026-09-06", tomorrow.AttendanceDate);
        Assert.False(P(tomorrow).IsExcluded);
        Assert.Contains("김민수", await service.BuildBoardCsvAsync(Meeting));
        Assert.Contains("p1", await _repository.GetMeetingExclusionsAsync(Meeting, "2026-09-05"));
    }

    [Fact]
    public async Task ExclusionFromPreviousDaysScreen_IsRejectedWithoutChangingToday()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 9, 5, 15, 1, 0, TimeSpan.Zero));
        var service = new AttendanceApplicationService(new ExcelRosterParser(), _repository, new AttendanceMatcher(), clock);
        var controller = new MeetingParticipantsController(service);
        Assert.Equal(409, Assert.IsType<ObjectResult>(await controller.SetExclusion(Meeting, "p1", new(true, "2026-09-05"), default)).StatusCode);
        Assert.False(P(await service.BuildBoardAsync(Meeting)).IsExcluded);
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test Korea", TimeSpan.FromHours(9), "Test Korea", "Test Korea");
    }

    [Fact]
    public async Task AllExcluded_ExportsOnlyHeader_AndDuplicateGroupsDisappear()
    {
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수"), new ParticipantSnapshotParticipant("b", "김민수") });
        foreach (var id in new[] { "p1", "p2", "p3" }) { await _service.SetMeetingExclusionAsync(Meeting, id, true); }
        var board = await _service.BuildBoardAsync(Meeting);
        Assert.All(board.People, p => Assert.True(p.IsExcluded));
        Assert.Empty(board.DuplicateConnectionGroups!);
        Assert.Single((await _service.BuildBoardCsvAsync(Meeting)).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Confirmation_IsDurableMeetingScopedAndDoesNotChangeMatchConfidence()
    {
        var initial = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") })).Board;
        var confirmed = await _service.SetPersonReviewAsync(Meeting, "p1", "identity", "confirmed", P(initial).IdentityEvidenceToken);
        Assert.Equal("confirmed", P(confirmed).IdentityReviewStatus);
        Assert.False(P(confirmed).ReviewRequired);
        Assert.Equal(MatchConfidence.NameOnly, P(confirmed).Confidence);
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") });
        var reloaded = await CreateService(new SqliteAttendanceRepository(_path)).BuildBoardAsync(Meeting);
        Assert.Equal("confirmed", P(reloaded).IdentityReviewStatus);
        Assert.Contains("\"confirmed\"", await _service.BuildBoardCsvAsync(Meeting));
        var other = (await Snapshot(meeting: "another", people: new[] { new ParticipantSnapshotParticipant("a", "김민수") })).Board;
        Assert.Equal("pending", P(other).IdentityReviewStatus);
    }

    [Fact]
    public async Task Confirmation_InvalidatesWhenEvidenceChanges_AndRejectsOldToken()
    {
        var initial = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") })).Board;
        await _service.SetPersonReviewAsync(Meeting, "p1", "identity", "confirmed", P(initial).IdentityEvidenceToken);
        var changed = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수"), new ParticipantSnapshotParticipant("b", "김민수") })).Board;
        Assert.Equal("pending", P(changed).IdentityReviewStatus);
        Assert.True(P(changed).ReviewRequired);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetPersonReviewAsync(Meeting, "p1", "identity", "confirmed", P(initial).IdentityEvidenceToken));
    }

    [Fact]
    public async Task IdentityAndDuplicateConfirmation_AreSeparateAndUndoable()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수"), new ParticipantSnapshotParticipant("b", "김민수") })).Board;
        board = await _service.SetPersonReviewAsync(Meeting, "p1", "identity", "confirmed", P(board).IdentityEvidenceToken);
        Assert.True(P(board).ReviewRequired);
        board = await _service.SetPersonReviewAsync(Meeting, "p1", "duplicate", "confirmed", P(board).DuplicateEvidenceToken);
        Assert.False(P(board).ReviewRequired);
        Assert.True(P(board).HasDuplicateConnections);
        board = await _service.SetPersonReviewAsync(Meeting, "p1", "identity", "pending", P(board).IdentityEvidenceToken);
        Assert.True(P(board).ReviewRequired);
        Assert.Equal("confirmed", P(board).DuplicateReviewStatus);
    }

    [Fact]
    public async Task DeferredReview_RemainsInReviewQueue()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") })).Board;
        board = await _service.SetPersonReviewAsync(Meeting, "p1", "identity", "deferred", P(board).IdentityEvidenceToken);
        Assert.Equal("deferred", P(board).IdentityReviewStatus);
        Assert.True(P(board).ReviewRequired);
    }

    [Fact]
    public async Task ManualConnectionMatch_IsNotGlobalAlias_AndUndoRestoresUnmatched()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        board = await _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", "p1", C(board).EvidenceToken);
        Assert.True(C(board).ManualMatch);
        Assert.Equal("p1", C(board).MatchedRosterPersonId);
        Assert.Empty(board.UnmatchedParticipants);
        Assert.Equal(AttendanceState.Present, P(board).AttendanceState);
        Assert.Equal(1, P(board).JoinCount);
        Assert.Equal(MatchConfidence.Verified, P(board).Confidence);
        Assert.Empty(await _repository.GetAliasMapAsync());
        var other = (await Snapshot(meeting: "another", people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        Assert.Single(other.UnmatchedParticipants);
        board = await _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", null, C(board).EvidenceToken);
        Assert.False(C(board).ManualMatch);
        Assert.Single(board.UnmatchedParticipants);
        Assert.Equal(AttendanceState.NotJoined, P(board).AttendanceState);
        Assert.All(await _repository.GetParticipantEventsAsync(Meeting), evt => Assert.Null(evt.MatchedRosterPersonId));
    }

    [Fact]
    public async Task ManualMatch_ProjectsJoinAndLeaveHistoryWithoutRewritingEvents()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        await _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", "p1", C(board).EvidenceToken);
        board = (await Snapshot()).Board;
        Assert.Equal(AttendanceState.Left, P(board).AttendanceState);
        Assert.NotNull(P(board).LastJoinedAt);
        Assert.NotNull(P(board).LastLeftAt);
        Assert.All(board.RecentEvents, evt => Assert.Equal("p1", evt.MatchedRosterPersonId));
        Assert.All(await _repository.GetParticipantEventsAsync(Meeting), evt => Assert.Null(evt.MatchedRosterPersonId));
    }

    [Fact]
    public async Task ManualMatch_DoesNotFollowReusedConnectionKeyOrChangedName()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        await _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", "p1", C(board).EvidenceToken);
        board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "Guest") })).Board;
        Assert.False(C(board).ManualMatch);
        Assert.Null(C(board).MatchedRosterPersonId);
        await Snapshot();
        board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        Assert.False(C(board).ManualMatch);
        Assert.Null(C(board).MatchedRosterPersonId);
    }

    [Fact]
    public async Task RemappingReusedKeyAndUndo_PreserveEarlierSessionAttributionAfterRestart()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        await _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", "p1", C(board).EvidenceToken);
        var left = (await Snapshot()).Board;
        var earlierJoin = P(left).LastJoinedAt;
        var earlierLeft = P(left).LastLeftAt;
        board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        board = await _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", "p2", C(board).EvidenceToken);
        Assert.Equal(AttendanceState.Left, P(board).AttendanceState);
        Assert.Equal(earlierJoin, P(board).LastJoinedAt);
        Assert.Equal(earlierLeft, P(board).LastLeftAt);
        Assert.Equal(AttendanceState.Present, P(board, "p2").AttendanceState);
        var restarted = CreateService(new SqliteAttendanceRepository(_path));
        board = await restarted.BuildBoardAsync(Meeting);
        board = await restarted.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", null, C(board).EvidenceToken);
        Assert.Equal(AttendanceState.Left, P(board).AttendanceState);
        Assert.Equal(1, P(board).JoinCount);
        Assert.Equal(AttendanceState.NotJoined, P(board, "p2").AttendanceState);
        Assert.Contains("김민수", await restarted.BuildBoardCsvAsync(Meeting));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappingAfterIdentityChange_PreservesObservedAttendanceOnLeave(bool emailOnly)
    {
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "Guest", "old-device@example.com") });
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", emailOnly ? "Guest" : "iPad", "new-device@example.com") })).Board;
        var observedAt = C(board).LastSeenAt;
        board = await _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", "p1", C(board).EvidenceToken);
        Assert.Equal(1, P(board).JoinCount);
        Assert.Equal(observedAt, P(board).LastJoinedAt);
        board = (await Snapshot()).Board;
        Assert.Equal(AttendanceState.Left, P(board).AttendanceState);
        Assert.Equal(1, P(board).JoinCount);
        Assert.Equal(observedAt, P(board).LastJoinedAt);
        Assert.NotNull(P(board).LastLeftAt);
        Assert.Null((await _repository.GetParticipantEventsAsync(Meeting)).First(evt => evt.EventType == ParticipantEventType.Joined).MatchedRosterPersonId);
        Assert.Equal(AttendanceState.Left, P(await CreateService(new SqliteAttendanceRepository(_path)).BuildBoardAsync(Meeting)).AttendanceState);
    }

    [Fact]
    public async Task ManualObservation_DoesNotCountAnExistingAutomaticJoinTwice()
    {
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") });
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        board = await _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", "p1", C(board).EvidenceToken);
        Assert.Equal(1, P(board).JoinCount);
        Assert.Equal(_start.AddSeconds(10), P(board).LastJoinedAt);
    }

    [Fact]
    public async Task SamePresenceKeyInDifferentSources_IsNotTheSameConnection()
    {
        await Snapshot(source: "source-a", people: new[] { new ParticipantSnapshotParticipant("a", "iPad") });
        var board = (await Snapshot(source: "source-b", people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        board = await _service.SetConnectionMatchAsync(Meeting, "source-a", "a", "p1", C(board, source: "source-a").EvidenceToken);
        Assert.Equal("p1", C(board, source: "source-a").MatchedRosterPersonId);
        Assert.Null(C(board, source: "source-b").MatchedRosterPersonId);
        Assert.Single(board.UnmatchedParticipants);
    }

    [Fact]
    public async Task ConnectionDeferral_IsDurableAndInvalidatedByIdentityChange()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        board = await _service.SetConnectionReviewAsync(Meeting, "web-dashboard", "a", "deferred", C(board).EvidenceToken);
        Assert.Equal("deferred", C(board).ReviewStatus);
        board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        Assert.Equal("deferred", C(board).ReviewStatus);
        board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "Guest") })).Board;
        Assert.Equal("pending", C(board).ReviewStatus);
    }

    [Fact]
    public async Task StaleConnectionToken_CannotAssignNewlyChangedParticipant()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "iPad") })).Board;
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "Guest") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "a", "p1", C(board).EvidenceToken));
    }

    [Fact]
    public async Task ExcludedPerson_CannotBeConfirmedOrReceiveNewManualMatch()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수"), new ParticipantSnapshotParticipant("b", "iPad") })).Board;
        await _service.SetMeetingExclusionAsync(Meeting, "p1", true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetPersonReviewAsync(Meeting, "p1", "identity", "confirmed", P(board).IdentityEvidenceToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetConnectionMatchAsync(Meeting, "web-dashboard", "b", "p1", C(board, "b").EvidenceToken));
    }

    [Fact]
    public async Task Freshness_UsesSnapshotReceipt_NotReadOrDecisionTime()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") })).Board;
        var captured = board.LastReceivedAt;
        board = await _service.SetMeetingExclusionAsync(Meeting, "p2", true);
        Assert.Equal(captured, board.LastReceivedAt);
        Assert.True(board.GeneratedAt > board.LastReceivedAt);
        Assert.Equal(captured, (await _service.BuildBoardAsync(Meeting)).LastReceivedAt);
        Assert.Equal(captured, Assert.Single(board.SnapshotSources!).CapturedAt);
        Assert.Null((await _service.BuildBoardAsync("empty-meeting")).LastReceivedAt);
    }

    [Fact]
    public async Task Freshness_DescribesAuthoritativeSource_NotNewerIgnoredSource()
    {
        var live = await Snapshot(source: "zoom-live-participants", people: new[] { new ParticipantSnapshotParticipant("a", "김민수") });
        var board = (await Snapshot(source: "web-dashboard", people: new[] { new ParticipantSnapshotParticipant("b", "이영희") })).Board;
        Assert.Equal(live.CapturedAt, board.LastReceivedAt);
        Assert.Equal("zoom-live-participants", Assert.Single(board.SnapshotSources!).Source);
        // Non-authoritative history is retained, but it must not establish current presence.
        Assert.NotEqual(AttendanceState.Present, P(board, "p2").AttendanceState);
    }

    [Fact]
    public async Task EventOnlyBoard_DoesNotInventSnapshotReceipt()
    {
        await _service.RecordParticipantEventAsync(new(Meeting, _start, ParticipantEventType.Joined, "김민수", null, "webhook", "{}"));
        var board = await _service.BuildBoardAsync(Meeting);
        Assert.Null(board.LastReceivedAt);
        Assert.Empty(board.SnapshotSources!);
        Assert.Equal(_start, board.LatestEventAt);
    }

    [Theory]
    [InlineData("identity", "unknown")]
    [InlineData("unknown", "confirmed")]
    public async Task InvalidReview_IsRejected(string kind, string status)
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.SetPersonReviewAsync(Meeting, "p1", kind, status, ""));

    [Fact]
    public async Task Controller_ValidatesMissingInputUnknownPersonAndStaleEvidence()
    {
        var controller = new MeetingParticipantsController(_service);
        Assert.Equal(400, Assert.IsType<ObjectResult>(await controller.SetExclusion(Meeting, "p1", new(null), default)).StatusCode);
        Assert.Equal(400, Assert.IsType<ObjectResult>(await controller.SetExclusion("", "p1", new(true), default)).StatusCode);
        Assert.Equal(404, Assert.IsType<ObjectResult>(await controller.SetExclusion(Meeting, "missing", new(true), default)).StatusCode);
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수") });
        Assert.Equal(409, Assert.IsType<ObjectResult>(await controller.SetPersonReview(Meeting, "p1", new("identity", "confirmed", "stale"), default)).StatusCode);
        var result = Assert.IsType<OkObjectResult>(await controller.SetExclusion("123 456 7890", "p1", new(true), default));
        Assert.True(P(Assert.IsType<AttendanceBoard>(result.Value)).IsExcluded);
    }

    [Fact]
    public void MissingMatchTargetIsNotSilentlyTreatedAsUndo()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MeetingConnectionMatchRequest>("{\"Source\":\"s\",\"PresenceKey\":\"a\"}"));
}
