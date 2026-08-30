using ZoomCheck.Core.Models;

namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomAppPairingCodeResponse(
    string Code,
    DateTimeOffset ExpiresAt,
    string? HomeUrl);

public sealed record ZoomAppConnectRequest(
    string? PairingCode,
    string? MeetingId,
    string? MeetingUuid,
    string? Role,
    IReadOnlyList<string>? SupportedApis);

public sealed record ZoomAppConnectResponse(
    string SessionToken,
    long SyncRevision,
    DateTimeOffset ExpiresAt);

public sealed record ZoomAppSnapshotRequest(
    string? SessionToken,
    string? MeetingId,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<ZoomAppParticipantRequest>? Participants);

public sealed record ZoomAppParticipantRequest(
    string? ParticipantUuid,
    string? ScreenName,
    string? Role);

public sealed record ZoomAppSnapshotResponse(
    int ActiveParticipants,
    ParticipantSnapshotResult Snapshot);

public sealed record ZoomAppHeartbeatRequest(
    string? SessionToken,
    long LastRevision);

public sealed record ZoomAppHeartbeatResponse(
    long SyncRevision,
    bool SyncRequested,
    DateTimeOffset ServerTime);

public sealed record ZoomRelaySnapshotPayload(
    string? MeetingId,
    string? MeetingUuid,
    string? Role,
    IReadOnlyList<string>? SupportedApis,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<ZoomAppParticipantRequest>? Participants);

public sealed record ZoomRelayHeartbeatPayload(
    string? MeetingId,
    string? MeetingUuid,
    string? Role,
    IReadOnlyList<string>? SupportedApis,
    DateTimeOffset? SentAt);

public sealed record ZoomAppBridgeStatus(
    bool Connected,
    string? MeetingId,
    string? MeetingUuid,
    string? Role,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? LastSnapshotAt,
    int ActiveParticipants,
    long SyncRevision,
    string? HomeUrl,
    DateTimeOffset? PairingCodeExpiresAt,
    string Transport = "direct",
    bool SessionActive = false);
