using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public sealed record BoardPersonStatus(
    string RosterPersonId,
    string Sequence,
    string Name,
    string Organization,
    /// <summary>Roster group (조/분반/그룹/팀/반), or empty when the roster has no group column.</summary>
    string Group,
    AttendanceState AttendanceState,
    MatchConfidence Confidence,
    string ConfidenceReason,
    DateTimeOffset? LastJoinedAt,
    DateTimeOffset? LastLeftAt,
    int JoinCount,
    /// <summary>How many active connections currently resolve to this person (0 when absent).</summary>
    int ActiveConnectionCount = 0,
    /// <summary>True when more than one active connection resolves to this person.</summary>
    bool HasDuplicateConnections = false,
    bool IsExcluded = false,
    string IdentityReviewStatus = "none",
    string DuplicateReviewStatus = "none",
    bool ReviewRequired = false,
    string IdentityEvidenceToken = "",
    string DuplicateEvidenceToken = "");
