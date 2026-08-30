using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

public sealed class ZoomAppBridgeTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"zoomcheck-zoom-app-{Guid.NewGuid():N}.db");

    private SqliteAttendanceRepository _repository = null!;
    private ZoomAppBridgeService _bridge = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteAttendanceRepository(_databasePath);
        await _repository.InitializeAsync();
        var attendance = new AttendanceApplicationService(
            new ExcelRosterParser(),
            _repository,
            new AttendanceMatcher());
        await _repository.ReplaceRosterAsync(new RosterImportResult(
            "roster",
            "test.xlsx",
            "test.xlsx",
            DateTimeOffset.UtcNow,
            new[]
            {
                Person("p1", "유영인"),
                Person("p2", "이순신")
            }));

        _bridge = new ZoomAppBridgeService(
            attendance,
            Options.Create(new ZoomAppOptions
            {
                HomeUrl = "https://zoomcheck.example/zoom-app/"
            }),
            TimeProvider.System);
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
    public async Task PairConnectAndSnapshot_AppliesLiveAttendanceForCoHost()
    {
        var pairing = _bridge.CreatePairingCode();
        var connection = _bridge.Connect(new ZoomAppConnectRequest(
            pairing.Code,
            "123 456 789",
            "meeting-uuid",
            "coHost",
            RequiredApis));

        var result = await _bridge.ApplySnapshotAsync(new ZoomAppSnapshotRequest(
            connection.SessionToken,
            "123456789",
            DateTimeOffset.UtcNow,
            new[]
            {
                new ZoomAppParticipantRequest("uuid-1", "영인 유", "coHost"),
                new ZoomAppParticipantRequest("uuid-2", "이순신", "attendee")
            }));

        Assert.Equal(2, result.ActiveParticipants);
        Assert.Equal("123456789", result.Snapshot.Board.MeetingId);
        Assert.All(result.Snapshot.Board.People, person => Assert.Equal(AttendanceState.Present, person.AttendanceState));
        Assert.Contains(result.Snapshot.Board.CurrentConnections!, item =>
            item.PresenceKey == "zoom-app:uuid-1"
            && item.RawName == "영인 유"
            && item.DisplayName == "유영인");

        var status = _bridge.GetStatus();
        Assert.True(status.Connected);
        Assert.Equal("coHost", status.Role);
        Assert.Equal(2, status.ActiveParticipants);
    }

    [Fact]
    public void Connect_RejectsAttendeeEvenWhenSdkApisExist()
    {
        var pairing = _bridge.CreatePairingCode();

        var error = Assert.Throws<ZoomAppBridgeException>(() => _bridge.Connect(new ZoomAppConnectRequest(
            pairing.Code,
            "meeting-1",
            null,
            "attendee",
            RequiredApis)));

        Assert.Equal(ZoomAppBridgeError.InsufficientRole, error.Error);
    }

    [Fact]
    public void PairingCode_IsSingleUse_AndSyncRevisionIsDeliveredByHeartbeat()
    {
        var pairing = _bridge.CreatePairingCode();
        var connection = _bridge.Connect(new ZoomAppConnectRequest(
            pairing.Code,
            "meeting-1",
            null,
            "host",
            RequiredApis));

        Assert.Throws<ZoomAppBridgeException>(() => _bridge.Connect(new ZoomAppConnectRequest(
            pairing.Code,
            "meeting-1",
            null,
            "host",
            RequiredApis)));

        var revision = _bridge.RequestSync();
        var heartbeat = _bridge.Heartbeat(new ZoomAppHeartbeatRequest(connection.SessionToken, 1));

        Assert.Equal(revision, heartbeat.SyncRevision);
        Assert.True(heartbeat.SyncRequested);
    }

    [Fact]
    public async Task EmptySnapshot_MustBeConfirmedBeforeEveryoneIsMarkedLeft()
    {
        var pairing = _bridge.CreatePairingCode();
        var connection = _bridge.Connect(new ZoomAppConnectRequest(
            pairing.Code,
            "meeting-1",
            null,
            "host",
            RequiredApis));
        await _bridge.ApplySnapshotAsync(new ZoomAppSnapshotRequest(
            connection.SessionToken,
            "meeting-1",
            DateTimeOffset.UtcNow,
            new[] { new ZoomAppParticipantRequest("uuid-1", "이순신", "attendee") }));

        var firstEmpty = await Assert.ThrowsAsync<ZoomAppBridgeException>(() =>
            _bridge.ApplySnapshotAsync(new ZoomAppSnapshotRequest(
                connection.SessionToken,
                "meeting-1",
                DateTimeOffset.UtcNow,
                Array.Empty<ZoomAppParticipantRequest>())));
        Assert.Equal(ZoomAppBridgeError.UnconfirmedEmptySnapshot, firstEmpty.Error);
        Assert.Equal(1, _bridge.GetStatus().ActiveParticipants);

        var confirmed = await _bridge.ApplySnapshotAsync(new ZoomAppSnapshotRequest(
            connection.SessionToken,
            "meeting-1",
            DateTimeOffset.UtcNow,
            Array.Empty<ZoomAppParticipantRequest>()));

        Assert.Equal(0, confirmed.ActiveParticipants);
        Assert.Equal(new[] { "이순신" }, confirmed.Snapshot.LeftNames);
    }

    [Fact]
    public async Task RelaySnapshot_UsesSameAttendanceEngineWithoutDirectSessionToken()
    {
        var result = await _bridge.ApplyRelaySnapshotAsync(new ZoomRelaySnapshotPayload(
            "123 456 789",
            "meeting-uuid",
            "coHost",
            RequiredApis,
            DateTimeOffset.UtcNow,
            new[]
            {
                new ZoomAppParticipantRequest("uuid-1", "영인 유", "coHost"),
                new ZoomAppParticipantRequest("uuid-2", "이순신", "attendee")
            }));

        Assert.Equal(2, result.ActiveParticipants);
        Assert.All(result.Snapshot.Board.People, person => Assert.Equal(AttendanceState.Present, person.AttendanceState));
        Assert.Contains(result.Snapshot.Board.CurrentConnections!, connection =>
            connection.PresenceKey == "zoom-app:uuid-1" && connection.DisplayName == "유영인");
    }

    [Fact]
    public void RelayHeartbeat_RejectsAttendeeAndMissingSdkApis()
    {
        var attendee = Assert.Throws<ZoomAppBridgeException>(() => _bridge.ValidateRelayHeartbeat(
            new ZoomRelayHeartbeatPayload("meeting-1", null, "attendee", RequiredApis, DateTimeOffset.UtcNow)));
        var missingApi = Assert.Throws<ZoomAppBridgeException>(() => _bridge.ValidateRelayHeartbeat(
            new ZoomRelayHeartbeatPayload("meeting-1", null, "host", new[] { "getMeetingContext" }, DateTimeOffset.UtcNow)));

        Assert.Equal(ZoomAppBridgeError.InsufficientRole, attendee.Error);
        Assert.Equal(ZoomAppBridgeError.UnsupportedClient, missingApi.Error);
    }

    private static readonly string[] RequiredApis =
    {
        "getMeetingParticipants",
        "getMeetingContext",
        "getUserContext"
    };

    private static RosterPerson Person(string id, string name)
        => new(
            id,
            id,
            name,
            NameNormalizer.Normalize(name),
            string.Empty,
            string.Empty,
            string.Empty,
            new[] { NameNormalizer.Normalize(name) });
}
