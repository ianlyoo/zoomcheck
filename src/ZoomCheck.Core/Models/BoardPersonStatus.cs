using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public sealed record BoardPersonStatus(
    string RosterPersonId,
    string Sequence,
    string Name,
    string Organization,
    AttendanceState AttendanceState,
    MatchConfidence Confidence,
    string ConfidenceReason,
    DateTimeOffset? LastJoinedAt,
    DateTimeOffset? LastLeftAt,
    int JoinCount);
