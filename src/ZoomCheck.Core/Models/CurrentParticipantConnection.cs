using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

/// <summary>
/// One currently connected participant, expanded with everything the dashboard needs to render
/// a participant row detail without re-parsing raw payloads.
/// </summary>
public sealed record CurrentParticipantConnection(
    string PresenceKey,
    string Source,
    string RawName,
    string DisplayName,
    string? CanonicalName,
    string NormalizedName,
    string? Email,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string? MatchedRosterPersonId,
    string? MatchedRosterPersonName,
    MatchConfidence Confidence,
    double MatchScore,
    string MatchReason);
