using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public sealed record MatchCandidate(
    RosterPerson? Person,
    MatchConfidence Confidence,
    double Score,
    string Reason)
{
    public static MatchCandidate Unmatched(string reason) => new(null, MatchConfidence.Unmatched, 0, reason);
}
