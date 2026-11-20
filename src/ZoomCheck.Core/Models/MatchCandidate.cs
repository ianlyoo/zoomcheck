using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public static class MatchReviewReasons
{
    public const string None = "";
    public const string DuplicateRosterEmail = "duplicate_roster_email";
    public const string IdentifierConflict = "identifier_conflict";
    public const string EmailConflict = "email_conflict";
    public const string DuplicateRosterName = "duplicate_roster_name";
    public const string BlankName = "blank_name";
    public const string PossibleMatch = "possible_match";
    public const string AmbiguousPossibleMatch = "ambiguous_possible_match";
    public const string Unmatched = "unmatched";
}

public sealed record MatchCandidate(
    RosterPerson? Person,
    MatchConfidence Confidence,
    double Score,
    string Reason,
    bool NeedsReview = false,
    string ReviewReasonCode = "")
{
    public static MatchCandidate Unmatched(string reason)
        => new(null, MatchConfidence.Unmatched, 0, reason, NeedsReview: true, ReviewReasonCode: MatchReviewReasons.Unmatched);
}
