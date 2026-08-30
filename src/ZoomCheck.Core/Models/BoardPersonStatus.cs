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
    int JoinCount,
    /// <summary>How many active connections currently resolve to this person (0 when absent).</summary>
    int ActiveConnectionCount = 0,
    /// <summary>True when more than one active connection resolves to this person.</summary>
    bool HasDuplicateConnections = false);
