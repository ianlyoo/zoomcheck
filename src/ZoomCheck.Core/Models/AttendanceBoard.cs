using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public sealed record AttendanceBoard(
    string MeetingId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<BoardPersonStatus> People,
    IReadOnlyList<UnmatchedParticipantStatus> UnmatchedParticipants,
    IReadOnlyList<ParticipantEvent> RecentEvents,
    IReadOnlyDictionary<MatchConfidence, int> ConfidenceCounts,
    IReadOnlyList<CurrentParticipantConnection>? CurrentConnections = null,
    IReadOnlyList<DuplicateConnectionGroup>? DuplicateConnectionGroups = null,
    /// <summary>
    /// Distinct non-empty roster groups in roster order, so filters can be offered without
    /// re-deriving them. Empty when the roster carries no group column.
    /// </summary>
    IReadOnlyList<string>? Groups = null,
    IReadOnlyList<ParticipantSnapshotSourceState>? SnapshotSources = null,
    DateTimeOffset? LastReceivedAt = null,
    DateTimeOffset? LatestEventAt = null,
    string AttendanceDate = "");
