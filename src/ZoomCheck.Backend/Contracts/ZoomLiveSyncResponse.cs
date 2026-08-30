using ZoomCheck.Core.Models;

namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomLiveSyncResponse(
    string MeetingId,
    DateTimeOffset SyncedAt,
    int ApiRecords,
    int ActiveParticipants,
    int IgnoredRecords,
    ParticipantSnapshotResult Snapshot,
    /// <summary>Every connection Zoom currently reports, with match and identity detail.</summary>
    IReadOnlyList<CurrentParticipantConnection>? CurrentConnections = null,
    /// <summary>Roster people currently matched by more than one active connection (review only).</summary>
    IReadOnlyList<DuplicateConnectionGroup>? DuplicateConnectionGroups = null,
    /// <summary>Display-name changes observed on connections that were already present.</summary>
    IReadOnlyList<ParticipantNameChange>? NameChanges = null);
