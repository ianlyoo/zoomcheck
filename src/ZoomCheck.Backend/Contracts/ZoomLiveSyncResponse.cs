using ZoomCheck.Core.Models;

namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomLiveSyncResponse(
    string MeetingId,
    DateTimeOffset SyncedAt,
    int ApiRecords,
    int ActiveParticipants,
    int IgnoredRecords,
    ParticipantSnapshotResult Snapshot);
