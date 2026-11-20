using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
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
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수 (태블릿)") });
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
        var initial = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수", "other@example.com") })).Board;
        var confirmed = await _service.SetPersonReviewAsync(Meeting, "p1", "identity", "confirmed", P(initial).IdentityEvidenceToken);
        Assert.Equal("confirmed", P(confirmed).IdentityReviewStatus);
        Assert.False(P(confirmed).ReviewRequired);
        Assert.Equal(MatchConfidence.NameOnly, P(confirmed).Confidence);
        await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수", "other@example.com") });
        var reloaded = await CreateService(new SqliteAttendanceRepository(_path)).BuildBoardAsync(Meeting);
        Assert.Equal("confirmed", P(reloaded).IdentityReviewStatus);
        Assert.Contains("\"confirmed\"", await _service.BuildBoardCsvAsync(Meeting));
        var other = (await Snapshot(meeting: "another", people: new[] { new ParticipantSnapshotParticipant("a", "김민수", "other@example.com") })).Board;
        Assert.Equal("pending", P(other).IdentityReviewStatus);
    }

    [Fact]
    public async Task Confirmation_InvalidatesWhenEvidenceChanges_AndRejectsOldToken()
    {
        var initial = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수", "other@example.com") })).Board;
        await _service.SetPersonReviewAsync(Meeting, "p1", "identity", "confirmed", P(initial).IdentityEvidenceToken);
        var changed = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수", "other@example.com"), new ParticipantSnapshotParticipant("b", "김민수", "other@example.com") })).Board;
        Assert.Equal("pending", P(changed).IdentityReviewStatus);
        Assert.True(P(changed).ReviewRequired);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetPersonReviewAsync(Meeting, "p1", "identity", "confirmed", P(initial).IdentityEvidenceToken));
    }

    [Fact]
    public async Task IdentityAndDuplicateConfirmation_AreSeparateAndUndoable()
    {
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수", "other@example.com"), new ParticipantSnapshotParticipant("b", "김민수", "other@example.com") })).Board;
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
        var board = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", "김민수", "other@example.com") })).Board;
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

    [Fact]
    public async Task CleanExactUniqueNameMatch_IsNotFlaggedForReview_WhilePresentLeftOrEventOnly()
    {
        var present = (await Snapshot(people: new[] { new ParticipantSnapshotParticipant("a", " 김 민 수 ") })).Board;
        Assert.Equal(AttendanceState.Present, P(present).AttendanceState);
        Assert.Equal(MatchConfidence.NameOnly, P(present).Confidence);
        Assert.Equal("none", P(present).IdentityReviewStatus);
        Assert.Equal("none", P(present).DuplicateReviewStatus);
        Assert.False(P(present).ReviewRequired);
        Assert.Equal(string.Empty, P(present).IdentityReviewReason);

        var left = (await Snapshot()).Board;
        Assert.Equal(AttendanceState.Left, P(left).AttendanceState);
        Assert.Equal("none", P(left).IdentityReviewStatus);
        Assert.False(P(left).ReviewRequired);

        await _service.RecordParticipantEventAsync(new("event-only", _start, ParticipantEventType.Joined, "김민수", null, "webhook", "{}"));
        var eventBoard = await _service.BuildBoardAsync("event-only");
        Assert.Equal(AttendanceState.Present, P(eventBoard).AttendanceState);
        Assert.False(P(eventBoard).ReviewRequired);
    }

    [Fact]
    public async Task ReviewQueue_FlagsHomonymsConflictsFuzzyDuplicatesAndUnmatched_Only()
    {
        await _repository.ReplaceRosterAsync(new RosterImportResult("review-cases", "roster.xlsx", "roster.xlsx", _start, new[]
        {
            Person("r1", "101", "학생001", "student001@example.com", "1조"),
            Person("r2", "102", "학생002", "student002@example.com", "1조"),
            Person("r3a", "103", "학생003", "student003a@example.com", "1조"),
            Person("r3b", "104", "학생003", "student003b@example.com", "1조"),
            Person("r5", "105", "학생005", "student005@example.com", "1조"),
            Person("r6", "106", "학생006", "student006@example.com", "2조"),
            Person("r7a", "107", "학생007", "shared@example.com", "2조"),
            Person("r7b", "108", "학생008", "shared@example.com", "2조"),
            Person("r120", "120", "학생120", "student120@example.com", "2조"),
            Person("r121", "121", "학생121", "student121@example.com", "2조")
        }));

        var board = (await Snapshot(people: new[]
        {
            new ParticipantSnapshotParticipant("clean", "학생001"),
            new ParticipantSnapshotParticipant("conflict-email", "학생002", "stranger@example.com"),
            new ParticipantSnapshotParticipant("homonym", "학생003"),
            new ParticipantSnapshotParticipant("fuzzy", "학생005 (태블릿)"),
            new ParticipantSnapshotParticipant("dup-1", "학생006"),
            new ParticipantSnapshotParticipant("dup-2", "학생006"),
            new ParticipantSnapshotParticipant("shared-email", "iPad", "shared@example.com"),
            new ParticipantSnapshotParticipant("cross-person", "학생121", "student120@example.com"),
            new ParticipantSnapshotParticipant("unmatched", "Guest 12")
        })).Board;

        // Clean exact unique name match: not flagged.
        Assert.False(P(board, "r1").ReviewRequired);
        Assert.Equal("none", P(board, "r1").IdentityReviewStatus);

        // Name-only match with conflicting email: flagged.
        Assert.True(P(board, "r2").ReviewRequired);
        Assert.Equal("pending", P(board, "r2").IdentityReviewStatus);
        Assert.Equal(MatchReviewReasons.EmailConflict, P(board, "r2").IdentityReviewReason);

        // Same name twice on the roster: flagged.
        Assert.True(P(board, "r3a").ReviewRequired);
        Assert.Equal("pending", P(board, "r3a").IdentityReviewStatus);
        Assert.Equal(MatchReviewReasons.DuplicateRosterName, P(board, "r3a").IdentityReviewReason);

        // Fuzzy/Possible match: flagged.
        Assert.True(P(board, "r5").ReviewRequired);
        Assert.Equal("pending", P(board, "r5").IdentityReviewStatus);
        Assert.Equal(MatchReviewReasons.PossibleMatch, P(board, "r5").IdentityReviewReason);

        // Duplicate active connections on a clean unique name: duplicate flagged, identity clean.
        Assert.True(P(board, "r6").ReviewRequired);
        Assert.Equal("none", P(board, "r6").IdentityReviewStatus);
        Assert.Equal("pending", P(board, "r6").DuplicateReviewStatus);

        // Duplicate roster email: flagged.
        Assert.True(P(board, "r7a").ReviewRequired);
        Assert.Equal("pending", P(board, "r7a").IdentityReviewStatus);
        Assert.Equal(MatchReviewReasons.DuplicateRosterEmail, P(board, "r7a").IdentityReviewReason);

        // One connection matching two people (email -> r120, display name -> r121): flagged.
        Assert.True(P(board, "r120").ReviewRequired);
        Assert.Equal("pending", P(board, "r120").IdentityReviewStatus);
        Assert.Equal(MatchReviewReasons.IdentifierConflict, P(board, "r120").IdentityReviewReason);

        // Unmatched off-roster participant: kept in unmatched review list.
        Assert.Single(board.UnmatchedParticipants);
        Assert.Equal("pending", C(board, "unmatched").ReviewStatus);

        // Flagged identity cases stay flagged after leaving.
        var afterLeave = (await Snapshot()).Board;
        Assert.False(P(afterLeave, "r1").ReviewRequired);
        Assert.True(P(afterLeave, "r2").ReviewRequired);
        Assert.True(P(afterLeave, "r3a").ReviewRequired);
        Assert.True(P(afterLeave, "r5").ReviewRequired);
        Assert.True(P(afterLeave, "r7a").ReviewRequired);
        Assert.True(P(afterLeave, "r120").ReviewRequired);
    }

    [Fact]
    public async Task Synthetic300Dataset_ReducesReviewQueueToOnlyAmbiguousDuplicateAndUnmatchedRows()
    {
        var rosterPeople = Enumerable.Range(1, 300)
            .Select(i => Person($"p{i:D3}", i.ToString(), $"학생{i:D3}", $"student{i:D3}@example.com", $"{(i - 1) / 30 + 1}조"))
            .ToArray();
        await _repository.ReplaceRosterAsync(new RosterImportResult("syn-300", "roster-300.xlsx", "roster-300.xlsx", _start, rosterPeople));

        var skipped = new HashSet<int> { 17, 34, 51, 68, 85, 102, 119, 136, 153, 170, 187, 204, 221, 238 };
        var names = Enumerable.Range(1, 240)
            .Where(i => !skipped.Contains(i))
            .Select(i => $"학생{i:D3}")
            .Concat(new[] { "학생010_1조", "학생12", "iPhone", "Guest 12", "김민수", "갤럭시 S24", "학생005 (태블릿)", "선생님" })
            .ToArray();

        var result = await _service.ApplyParticipantSnapshotAsync(new(Meeting, names, "web-dashboard", _start.AddMinutes(1)));
        var board = result.Board;

        Assert.Equal(300, board.People.Count);
        Assert.Equal(226, board.People.Count(p => p.AttendanceState == AttendanceState.Present));
        Assert.Equal(74, board.People.Count(p => p.AttendanceState != AttendanceState.Present));
        Assert.Equal(3, board.People.Count(p => p.ReviewRequired));
        Assert.Equal(5, board.UnmatchedParticipants.Count);
        Assert.Equal(8, board.People.Count(p => p.ReviewRequired) + board.UnmatchedParticipants.Count);
        Assert.Equal(new[] { "p005", "p010", "p012" }, board.People.Where(p => p.ReviewRequired).Select(p => p.RosterPersonId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task V063Upgrade_PreservesSavedConfirmedAndDeferredIdentityAndDuplicateDecisions()
    {
        var upgradeDbPath = Path.Combine(Path.GetTempPath(), $"zoomcheck-v063-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            static string V063Evidence(object value)
                => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

            var joinedAt = new DateTimeOffset(2026, 9, 5, 1, 0, 0, TimeSpan.Zero);
            var joinedIso = joinedAt.ToString("O");

            // Compute exact v0.6.3 tokens (without NeedsReview / ReviewReasonCode / IdentityReviewReason).
            var s005Conn1 = V063Evidence(new
            {
                Source = "zoom-app",
                PresenceKey = "fake-학생005-1",
                RawName = "학생005",
                Email = "",
                FirstSeenAt = joinedAt,
                MatchedRosterPersonId = "p005",
                Confidence = MatchConfidence.NameOnly,
                ManualMatch = false
            });
            var s005Conn2 = V063Evidence(new
            {
                Source = "zoom-app",
                PresenceKey = "fake-학생005 (태블릿)-1",
                RawName = "학생005 (태블릿)",
                Email = "",
                FirstSeenAt = joinedAt,
                MatchedRosterPersonId = "p005",
                Confidence = MatchConfidence.Possible,
                ManualMatch = false
            });
            var s005Conns = new[] { s005Conn1, s005Conn2 }.OrderBy(t => t, StringComparer.Ordinal).ToArray();
            var s005IdentityToken = V063Evidence(new
            {
                RosterPersonId = "p005",
                Confidence = MatchConfidence.Possible,
                LastJoinedAt = (DateTimeOffset?)joinedAt,
                LastLeftAt = (DateTimeOffset?)null,
                Connections = s005Conns
            });
            var s005DuplicateToken = V063Evidence(new
            {
                RosterPersonId = "p005",
                Connections = s005Conns
            });

            var s010Conn1 = V063Evidence(new
            {
                Source = "zoom-app",
                PresenceKey = "fake-학생010-1",
                RawName = "학생010",
                Email = "",
                FirstSeenAt = joinedAt,
                MatchedRosterPersonId = "p010",
                Confidence = MatchConfidence.NameOnly,
                ManualMatch = false
            });
            var s010Conn2 = V063Evidence(new
            {
                Source = "zoom-app",
                PresenceKey = "fake-학생010_1조-1",
                RawName = "학생010_1조",
                Email = "",
                FirstSeenAt = joinedAt,
                MatchedRosterPersonId = "p010",
                Confidence = MatchConfidence.Possible,
                ManualMatch = false
            });
            var s010Conns = new[] { s010Conn1, s010Conn2 }.OrderBy(t => t, StringComparer.Ordinal).ToArray();
            var s010DuplicateToken = V063Evidence(new
            {
                RosterPersonId = "p010",
                Connections = s010Conns
            });

            var unmatchedConnToken = V063Evidence(new
            {
                Source = "zoom-app",
                PresenceKey = "fake-iPhone-1",
                RawName = "iPhone",
                Email = "",
                FirstSeenAt = joinedAt,
                MatchedRosterPersonId = (string?)null,
                Confidence = MatchConfidence.Unmatched,
                ManualMatch = false
            });

            // Build a v0.6.3-shaped SQLite database directly (no occurred_at_ticks, no summary tables).
            var connectionString = new SqliteConnectionStringBuilder { DataSource = upgradeDbPath, Pooling = false }.ToString();
            await using (var conn = new SqliteConnection(connectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE roster_imports (import_id TEXT PRIMARY KEY, source_path TEXT NOT NULL, display_name TEXT NOT NULL, imported_at TEXT NOT NULL);
                    CREATE TABLE roster_people (id TEXT PRIMARY KEY, import_id TEXT NOT NULL, sequence TEXT NOT NULL, name TEXT NOT NULL, normalized_name TEXT NOT NULL, email TEXT NOT NULL, phone TEXT NOT NULL, organization TEXT NOT NULL, aliases_json TEXT NOT NULL, group_name TEXT NOT NULL DEFAULT '');
                    CREATE TABLE manual_aliases (alias_key TEXT PRIMARY KEY, roster_person_id TEXT NOT NULL, note TEXT NOT NULL, created_at TEXT NOT NULL);
                    CREATE TABLE participant_events (id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL, occurred_at TEXT NOT NULL, event_type TEXT NOT NULL, participant_name TEXT NOT NULL, normalized_participant_name TEXT NOT NULL, participant_email TEXT NULL, confidence TEXT NOT NULL, matched_roster_person_id TEXT NULL, source TEXT NOT NULL, raw_payload TEXT NOT NULL, presence_key TEXT NULL, raw_participant_name TEXT NULL, canonical_participant_name TEXT NULL, previous_participant_name TEXT NULL, previous_raw_participant_name TEXT NULL);
                    CREATE TABLE participant_snapshots (meeting_id TEXT NOT NULL, source TEXT NOT NULL, captured_at TEXT NOT NULL, present_count INTEGER NOT NULL, PRIMARY KEY (meeting_id, source));
                    CREATE TABLE participant_presence_v2 (meeting_id TEXT NOT NULL, source TEXT NOT NULL, presence_key TEXT NOT NULL, normalized_name TEXT NOT NULL, display_name TEXT NOT NULL, first_seen_at TEXT NOT NULL, last_seen_at TEXT NOT NULL, participant_email TEXT NULL, raw_display_name TEXT NULL, canonical_name TEXT NULL, PRIMARY KEY (meeting_id, source, presence_key));
                    CREATE TABLE schema_migrations (migration_id TEXT PRIMARY KEY, applied_at TEXT NOT NULL);
                    CREATE TABLE meeting_exclusions (meeting_id TEXT NOT NULL, attendance_date TEXT NOT NULL, roster_person_id TEXT NOT NULL, excluded_at TEXT NOT NULL, PRIMARY KEY (meeting_id, attendance_date, roster_person_id));
                    CREATE TABLE meeting_reviews (meeting_id TEXT NOT NULL, subject_key TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL, evidence_token TEXT NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY (meeting_id, subject_key, kind));
                    CREATE TABLE meeting_connection_observations (meeting_id TEXT NOT NULL, source TEXT NOT NULL, presence_key TEXT NOT NULL, first_seen_at TEXT NOT NULL, observed_name TEXT NOT NULL, observed_email TEXT NOT NULL, roster_person_id TEXT NOT NULL, observed_at TEXT NOT NULL, PRIMARY KEY (meeting_id, source, presence_key, first_seen_at, observed_name, observed_email));
                    """;
                await cmd.ExecuteNonQueryAsync();

                await using var seed = conn.CreateCommand();
                seed.CommandText = """
                    INSERT INTO schema_migrations VALUES ('meeting-id-v1', $t);
                    INSERT INTO roster_imports VALUES ('imp1', 'roster.xlsx', 'roster.xlsx', $t);
                    INSERT INTO roster_people VALUES ('p005', 'imp1', '5', '학생005', '학생005', 'student005@example.com', '', '', '[]', '1조');
                    INSERT INTO roster_people VALUES ('p010', 'imp1', '10', '학생010', '학생010', 'student010@example.com', '', '', '[]', '1조');
                    INSERT INTO participant_snapshots VALUES ($m, 'zoom-app', $t, 5);
                    INSERT INTO participant_presence_v2 VALUES ($m, 'zoom-app', 'fake-학생005-1', '학생005', '학생005', $t, $t, NULL, '학생005', NULL);
                    INSERT INTO participant_presence_v2 VALUES ($m, 'zoom-app', 'fake-학생005 (태블릿)-1', '학생005태블릿', '학생005 (태블릿)', $t, $t, NULL, '학생005 (태블릿)', NULL);
                    INSERT INTO participant_presence_v2 VALUES ($m, 'zoom-app', 'fake-학생010-1', '학생010', '학생010', $t, $t, NULL, '학생010', NULL);
                    INSERT INTO participant_presence_v2 VALUES ($m, 'zoom-app', 'fake-학생010_1조-1', '학생0101조', '학생010_1조', $t, $t, NULL, '학생010_1조', NULL);
                    INSERT INTO participant_presence_v2 VALUES ($m, 'zoom-app', 'fake-iPhone-1', 'iphone', 'iPhone', $t, $t, NULL, 'iPhone', NULL);
                    INSERT INTO participant_events VALUES ('e1', $m, $t, 'Joined', '학생005', '학생005', NULL, 'NameOnly', 'p005', 'zoom-app', '{}', 'fake-학생005-1', '학생005', NULL, NULL, NULL);
                    INSERT INTO participant_events VALUES ('e2', $m, $t, 'Joined', '학생005 (태블릿)', '학생005태블릿', NULL, 'Possible', 'p005', 'zoom-app', '{}', 'fake-학생005 (태블릿)-1', '학생005 (태블릿)', NULL, NULL, NULL);
                    INSERT INTO participant_events VALUES ('e3', $m, $t, 'Joined', '학생010', '학생010', NULL, 'NameOnly', 'p010', 'zoom-app', '{}', 'fake-학생010-1', '학생010', NULL, NULL, NULL);
                    INSERT INTO participant_events VALUES ('e4', $m, $t, 'Joined', '학생010_1조', '학생0101조', NULL, 'Possible', 'p010', 'zoom-app', '{}', 'fake-학생010_1조-1', '학생010_1조', NULL, NULL, NULL);
                    INSERT INTO participant_events VALUES ('e5', $m, $t, 'Joined', 'iPhone', 'iphone', NULL, 'Unmatched', NULL, 'zoom-app', '{}', 'fake-iPhone-1', 'iPhone', NULL, NULL, NULL);
                    INSERT INTO meeting_reviews VALUES ($m, 'p005', 'identity', 'confirmed', $tok005Id, $t);
                    INSERT INTO meeting_reviews VALUES ($m, 'p005', 'duplicate', 'deferred', $tok005Dup, $t);
                    INSERT INTO meeting_reviews VALUES ($m, 'p010', 'duplicate', 'confirmed', $tok010Dup, $t);
                    INSERT INTO meeting_reviews VALUES ($m, $connSubject, 'connection', 'deferred', $tokConn, $t);
                    """;
                seed.Parameters.AddWithValue("$m", Meeting);
                seed.Parameters.AddWithValue("$t", joinedIso);
                seed.Parameters.AddWithValue("$tok005Id", s005IdentityToken);
                seed.Parameters.AddWithValue("$tok005Dup", s005DuplicateToken);
                seed.Parameters.AddWithValue("$tok010Dup", s010DuplicateToken);
                seed.Parameters.AddWithValue("$connSubject", JsonSerializer.Serialize(new[] { "zoom-app", "fake-iPhone-1" }));
                seed.Parameters.AddWithValue("$tokConn", unmatchedConnToken);
                await seed.ExecuteNonQueryAsync();
            }

            // Start the new code against the v0.6.3 database.
            var upgradedRepo = new SqliteAttendanceRepository(upgradeDbPath);
            await upgradedRepo.InitializeAsync();
            var upgradedService = CreateService(upgradedRepo);
            var board = await upgradedService.BuildBoardAsync(Meeting);

            var p005 = P(board, "p005");
            Assert.Equal("confirmed", p005.IdentityReviewStatus);
            Assert.Equal("deferred", p005.DuplicateReviewStatus);
            Assert.True(p005.ReviewRequired);

            var p010 = P(board, "p010");
            Assert.Equal("pending", p010.IdentityReviewStatus);
            Assert.Equal("confirmed", p010.DuplicateReviewStatus);
            Assert.True(p010.ReviewRequired);

            var unmatched = C(board, "fake-iPhone-1", "zoom-app");
            Assert.Equal("deferred", unmatched.ReviewStatus);

            // Real evidence changes still invalidate the preserved decision.
            var changed = (await upgradedService.ApplyParticipantSnapshotAsync(new(
                Meeting,
                new[] { "학생005", "학생005 (태블릿)", "학생005 (폰)", "학생010", "학생010_1조", "iPhone" },
                "zoom-app",
                joinedAt.AddMinutes(1),
                Participants: new[]
                {
                    new ParticipantSnapshotParticipant("fake-학생005-1", "학생005"),
                    new ParticipantSnapshotParticipant("fake-학생005 (태블릿)-1", "학생005 (태블릿)"),
                    new ParticipantSnapshotParticipant("fake-학생005 (폰)-1", "학생005 (폰)"),
                    new ParticipantSnapshotParticipant("fake-학생010-1", "학생010"),
                    new ParticipantSnapshotParticipant("fake-학생010_1조-1", "학생010_1조"),
                    new ParticipantSnapshotParticipant("fake-iPhone-1", "iPhone")
                }))).Board;
            Assert.Equal("pending", P(changed, "p005").IdentityReviewStatus);
            Assert.Equal("pending", P(changed, "p005").DuplicateReviewStatus);
            Assert.Equal("confirmed", P(changed, "p010").DuplicateReviewStatus);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(upgradeDbPath + suffix)) { File.Delete(upgradeDbPath + suffix); }
            }
        }
    }
}
