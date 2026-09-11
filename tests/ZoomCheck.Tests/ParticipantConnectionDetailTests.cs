using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

public sealed class ParticipantConnectionDetailTests : IAsyncLifetime
{
    private const string ZoomSource = "zoom-live-participants";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"zoomcheck-connection-{Guid.NewGuid():N}.db");

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
                Person("p1", "유영인", "1", "youngin@example.com"),
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
    public async Task Snapshot_EmitsNameChangedEvent_WhenPresenceKeyKeepsIdentityButRenames()
    {
        await Apply("meeting-1", Connection("zoom-id:1", "이순신"));

        var renamed = await Apply("meeting-1", Connection("zoom-id:1", "이순신 iPhone"));

        // Same connection: not a join and not a leave.
        Assert.Empty(renamed.JoinedNames);
        Assert.Empty(renamed.LeftNames);
        Assert.Equal(1, renamed.PresentCount);

        var change = Assert.Single(renamed.NameChanges!);
        Assert.Equal("zoom-id:1", change.PresenceKey);
        Assert.Equal("이순신", change.PreviousName);
        Assert.Equal("이순신", change.PreviousRawName);
        Assert.Equal("이순신 iPhone", change.Name);
        Assert.Equal("이순신 iPhone", change.RawName);

        var nameChangedEvent = Assert.Single(
            renamed.Board.RecentEvents,
            evt => evt.EventType == ParticipantEventType.NameChanged);
        Assert.Equal("zoom-id:1", nameChangedEvent.PresenceKey);
        Assert.Equal("이순신", nameChangedEvent.PreviousParticipantName);
        Assert.Equal("이순신", nameChangedEvent.PreviousRawParticipantName);
        Assert.Equal("이순신 iPhone", nameChangedEvent.ParticipantName);
        Assert.Equal("이순신 iPhone", nameChangedEvent.RawParticipantName);
    }

    [Fact]
    public async Task Snapshot_NameChangedEvent_SurvivesPersistenceAndKeepsPresence()
    {
        await Apply("meeting-1", Connection("zoom-id:1", "이순신"));
        await Apply("meeting-1", Connection("zoom-id:1", "이순신 (전화)"));

        var persisted = await _service.GetParticipantEventsForMeetingAsync("meeting-1");
        var nameChanged = Assert.Single(persisted, evt => evt.EventType == ParticipantEventType.NameChanged);

        Assert.Equal("이순신", nameChanged.PreviousParticipantName);
        Assert.Equal("이순신 (전화)", nameChanged.RawParticipantName);
        Assert.Equal("zoom-id:1", nameChanged.PresenceKey);

        var board = await _service.BuildBoardAsync("meeting-1");
        Assert.Equal(
            AttendanceState.Present,
            board.People.Single(person => person.RosterPersonId == "p2").AttendanceState);
    }

    [Fact]
    public async Task Snapshot_DoesNotReportRename_ForWhitespaceOnlyDifference()
    {
        await Apply("meeting-1", Connection("zoom-id:1", "이순신"));

        var resubmitted = await Apply("meeting-1", Connection("zoom-id:1", "이 순 신"));

        Assert.Empty(resubmitted.NameChanges!);
        Assert.DoesNotContain(
            resubmitted.Board.RecentEvents,
            evt => evt.EventType == ParticipantEventType.NameChanged);
    }

    [Fact]
    public async Task Snapshot_CanonicalizesReorderedKoreanName_ButKeepsRawNameForDetail()
    {
        var result = await Apply("meeting-1", Connection("zoom-id:9", "영인 유"));

        var connection = Assert.Single(result.Board.CurrentConnections!);
        Assert.Equal("영인 유", connection.RawName);
        Assert.Equal("유영인", connection.DisplayName);
        Assert.Equal("유영인", connection.CanonicalName);
        Assert.Equal("p1", connection.MatchedRosterPersonId);

        Assert.Equal(
            AttendanceState.Present,
            result.Board.People.Single(person => person.RosterPersonId == "p1").AttendanceState);
        Assert.Empty(result.Board.UnmatchedParticipants);
    }

    [Fact]
    public async Task Snapshot_SavedAliasWinsOverConflictingAutomaticCanonicalization()
    {
        await _service.SaveAliasAsync("영인 유", "p2", "operator decision");

        var result = await Apply("meeting-alias", Connection("zoom-id:alias", "영인 유"));

        var connection = Assert.Single(result.Board.CurrentConnections!);
        Assert.Equal("영인 유", connection.RawName);
        Assert.Equal("영인 유", connection.DisplayName);
        Assert.Null(connection.CanonicalName);
        Assert.Equal("p2", connection.MatchedRosterPersonId);
        Assert.Equal(MatchConfidence.AliasVerified, connection.Confidence);
        Assert.Equal(
            AttendanceState.Present,
            result.Board.People.Single(person => person.RosterPersonId == "p2").AttendanceState);
    }

    [Fact]
    public async Task ManualSnapshot_RosterImportedLaterDoesNotCreateFalseLeaveAndJoin()
    {
        await _repository.ReplaceRosterAsync(new RosterImportResult(
            ImportId: "empty-roster",
            SourcePath: "empty.xlsx",
            DisplayName: "empty.xlsx",
            ImportedAt: DateTimeOffset.UtcNow,
            People: Array.Empty<RosterPerson>()));

        await _service.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
            "meeting-manual",
            new[] { "영인 유" },
            "manual-snapshot",
            DateTimeOffset.UtcNow));

        await _repository.ReplaceRosterAsync(new RosterImportResult(
            ImportId: "later-roster",
            SourcePath: "later.xlsx",
            DisplayName: "later.xlsx",
            ImportedAt: DateTimeOffset.UtcNow,
            People: new[] { Person("p1", "유영인", "1") }));

        var result = await _service.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
            "meeting-manual",
            new[] { "영인 유" },
            "manual-snapshot",
            DateTimeOffset.UtcNow));

        Assert.Empty(result.JoinedNames);
        Assert.Empty(result.LeftNames);
        Assert.Equal(1, result.PresentCount);
        Assert.Single(result.NameChanges!);
        Assert.Equal("유영인", Assert.Single(result.Board.CurrentConnections!).DisplayName);
    }

    [Fact]
    public async Task Board_ExposesCurrentConnectionDetail()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        await _service.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
            "meeting-1",
            Array.Empty<string>(),
            ZoomSource,
            capturedAt,
            ParticipantEmails: null,
            Participants: new[] { Connection("zoom-id:1", "누구세요", "youngin@example.com") }));

        var board = await _service.BuildBoardAsync("meeting-1");
        var connection = Assert.Single(board.CurrentConnections!);

        Assert.Equal("zoom-id:1", connection.PresenceKey);
        Assert.Equal(ZoomSource, connection.Source);
        Assert.Equal("누구세요", connection.RawName);
        Assert.Equal("누구세요", connection.DisplayName);
        Assert.Null(connection.CanonicalName);
        Assert.Equal(NameNormalizer.Normalize("누구세요"), connection.NormalizedName);
        Assert.Equal("youngin@example.com", connection.Email);
        Assert.Equal(capturedAt, connection.FirstSeenAt);
        Assert.Equal(capturedAt, connection.LastSeenAt);
        Assert.Equal("p1", connection.MatchedRosterPersonId);
        Assert.Equal("유영인", connection.MatchedRosterPersonName);
        Assert.Equal(MatchConfidence.Verified, connection.Confidence);
        Assert.Equal(1.0, connection.MatchScore);
        Assert.False(string.IsNullOrWhiteSpace(connection.MatchReason));
    }

    [Fact]
    public async Task Board_ReportsDuplicateGroup_AndStillCountsPersonPresentOnce()
    {
        var result = await Apply(
            "meeting-1",
            Connection("zoom-id:1", "이순신"),
            Connection("zoom-id:2", "이순신"));

        Assert.Equal(2, result.PresentCount);

        var board = result.Board;
        var duplicate = Assert.Single(board.DuplicateConnectionGroups!);
        Assert.Equal("p2", duplicate.RosterPersonId);
        Assert.Equal("이순신", duplicate.RosterPersonName);
        Assert.Equal(2, duplicate.ConnectionCount);
        Assert.Equal(
            new[] { "zoom-id:1", "zoom-id:2" },
            duplicate.Connections.Select(connection => connection.PresenceKey).OrderBy(key => key, StringComparer.Ordinal));

        // The duplicate is review metadata only: the person is present, counted exactly once.
        var person = board.People.Single(item => item.RosterPersonId == "p2");
        Assert.Equal(AttendanceState.Present, person.AttendanceState);
        Assert.Equal(2, person.ActiveConnectionCount);
        Assert.True(person.HasDuplicateConnections);

        Assert.Equal(1, board.People.Count(item => item.AttendanceState == AttendanceState.Present));
        Assert.Empty(board.UnmatchedParticipants);
    }

    [Fact]
    public async Task Board_LeavesNonDuplicatePeopleWithSingleConnection()
    {
        var result = await Apply("meeting-1", Connection("zoom-id:1", "이순신"));

        Assert.Empty(result.Board.DuplicateConnectionGroups!);

        var person = result.Board.People.Single(item => item.RosterPersonId == "p2");
        Assert.Equal(1, person.ActiveConnectionCount);
        Assert.False(person.HasDuplicateConnections);

        var absent = result.Board.People.Single(item => item.RosterPersonId == "p1");
        Assert.Equal(0, absent.ActiveConnectionCount);
        Assert.False(absent.HasDuplicateConnections);
    }

    [Fact]
    public async Task Snapshot_ConnectorIdChangeForUniquePerson_DoesNotCreateFalseLeaveAndJoin()
    {
        await Apply("meeting-1", Connection("zoom-id:dashboard-1", "이순신"));

        var switched = await Apply("meeting-1", Connection("zoom-app:uuid-1", "이순신"));

        Assert.Empty(switched.JoinedNames);
        Assert.Empty(switched.LeftNames);
        Assert.Single(switched.Board.CurrentConnections!);
        Assert.Equal("zoom-id:dashboard-1", switched.Board.CurrentConnections![0].PresenceKey);
        Assert.Equal(
            1,
            switched.Board.RecentEvents.Count(item =>
                item.ParticipantName == "이순신" && item.EventType == ParticipantEventType.Joined));
    }

    [Fact]
    public async Task Snapshot_ConnectorIdChangeForDuplicateNames_RemainsAmbiguous()
    {
        await Apply(
            "meeting-1",
            Connection("zoom-id:old-1", "동명이인"),
            Connection("zoom-id:old-2", "동명이인"));

        var switched = await Apply(
            "meeting-1",
            Connection("zoom-app:new-1", "동명이인"),
            Connection("zoom-app:new-2", "동명이인"));

        Assert.Equal(2, switched.JoinedNames.Count);
        Assert.Equal(2, switched.LeftNames.Count);
    }

    private static ParticipantSnapshotParticipant Connection(string presenceKey, string displayName, string? email = null)
        => new(presenceKey, displayName, email);

    private Task<ParticipantSnapshotResult> Apply(string meetingId, params ParticipantSnapshotParticipant[] participants)
        => _service.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
            meetingId,
            participants.Select(participant => participant.DisplayName).ToArray(),
            ZoomSource,
            DateTimeOffset.UtcNow,
            ParticipantEmails: null,
            Participants: participants));

    private static RosterPerson Person(string id, string name, string sequence, string email = "")
        => new(
            Id: id,
            Sequence: sequence,
            Name: name,
            NormalizedName: NameNormalizer.Normalize(name),
            Email: email,
            Phone: string.Empty,
            Organization: string.Empty,
            Aliases: Array.Empty<string>());
}
