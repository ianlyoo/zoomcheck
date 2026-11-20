using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;

namespace ZoomCheck.Core.Services;

public sealed class AttendanceMatcher
{
    public MatchCandidate Match(
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliasMap,
        string participantName,
        string? participantEmail)
    {
        var normalizedName = NameNormalizer.Normalize(participantName);
        var normalizedEmail = participantEmail?.Trim().ToLowerInvariant();

        IReadOnlyList<RosterPerson>? emailMatches = null;
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            emailMatches = FindEmailMatches(roster, normalizedEmail);
        }

        RosterPerson? aliasPerson = null;
        if (aliasMap.TryGetValue(normalizedName, out var aliasPersonId))
        {
            aliasPerson = roster.FirstOrDefault(person => person.Id == aliasPersonId);
        }

        var exactNameMatches = FindExactNameMatches(roster, normalizedName);

        return MatchResolved(roster, normalizedName, normalizedEmail, emailMatches, aliasPerson, exactNameMatches);
    }

    internal static MatchCandidate MatchResolved(
        IReadOnlyList<RosterPerson> roster,
        string normalizedName,
        string? normalizedEmail,
        IReadOnlyList<RosterPerson>? emailMatches,
        RosterPerson? aliasPerson,
        IReadOnlyList<RosterPerson> exactNameMatches)
    {
        if (emailMatches is { Count: > 0 })
        {
            if (emailMatches.Count > 1)
            {
                var picked = PickBestEmailCandidate(emailMatches, normalizedName);
                return new MatchCandidate(
                    picked,
                    MatchConfidence.Verified,
                    1.0,
                    $"Ambiguous email match ({emailMatches.Count} roster candidates)",
                    NeedsReview: true,
                    ReviewReasonCode: MatchReviewReasons.DuplicateRosterEmail);
            }

            var emailMatch = emailMatches[0];
            var conflictsWithAlias = aliasPerson is not null
                && !string.Equals(aliasPerson.Id, emailMatch.Id, StringComparison.Ordinal);
            var conflictsWithAnotherName = !string.IsNullOrEmpty(normalizedName)
                && HasOtherPerson(exactNameMatches, emailMatch.Id);
            if (conflictsWithAlias || conflictsWithAnotherName)
            {
                return new MatchCandidate(
                    emailMatch,
                    MatchConfidence.Verified,
                    1.0,
                    "Email exact match (display name matches another roster person)",
                    NeedsReview: true,
                    ReviewReasonCode: MatchReviewReasons.IdentifierConflict);
            }

            return new MatchCandidate(
                emailMatch,
                MatchConfidence.Verified,
                1.0,
                "Email exact match",
                NeedsReview: false,
                ReviewReasonCode: MatchReviewReasons.None);
        }

        if (aliasPerson is not null)
        {
            if (HasEmailConflict(aliasPerson.Email, normalizedEmail))
            {
                return new MatchCandidate(
                    aliasPerson,
                    MatchConfidence.AliasVerified,
                    0.98,
                    "Saved alias match (email differs from roster)",
                    NeedsReview: true,
                    ReviewReasonCode: MatchReviewReasons.EmailConflict);
            }

            return new MatchCandidate(
                aliasPerson,
                MatchConfidence.AliasVerified,
                0.98,
                "Saved alias match",
                NeedsReview: false,
                ReviewReasonCode: MatchReviewReasons.None);
        }

        if (exactNameMatches.Count > 0)
        {
            if (string.IsNullOrEmpty(normalizedName))
            {
                return new MatchCandidate(
                    exactNameMatches[0],
                    MatchConfidence.NameOnly,
                    0.9,
                    "Normalized name exact match",
                    NeedsReview: true,
                    ReviewReasonCode: MatchReviewReasons.BlankName);
            }

            if (exactNameMatches.Count > 1)
            {
                return new MatchCandidate(
                    exactNameMatches[0],
                    MatchConfidence.NameOnly,
                    0.9,
                    $"Ambiguous exact name match ({exactNameMatches.Count} roster candidates)",
                    NeedsReview: true,
                    ReviewReasonCode: MatchReviewReasons.DuplicateRosterName);
            }

            var nameMatch = exactNameMatches[0];
            if (HasEmailConflict(nameMatch.Email, normalizedEmail))
            {
                return new MatchCandidate(
                    nameMatch,
                    MatchConfidence.NameOnly,
                    0.9,
                    "Normalized name exact match (email differs from roster)",
                    NeedsReview: true,
                    ReviewReasonCode: MatchReviewReasons.EmailConflict);
            }

            return new MatchCandidate(
                nameMatch,
                MatchConfidence.NameOnly,
                0.9,
                "Normalized name exact match",
                NeedsReview: false,
                ReviewReasonCode: MatchReviewReasons.None);
        }

        return FindBestPossibleMatch(roster, normalizedName);
    }

    internal static MatchCandidate FindBestPossibleMatch(IReadOnlyList<RosterPerson> roster, string normalizedName)
    {
        RosterPerson? bestPerson = null;
        var bestScore = 0.0;
        var tiedBestCount = 0;

        for (var i = 0; i < roster.Count; i++)
        {
            var person = roster[i];
            var score = CalculateNameScore(normalizedName, person.NormalizedName);
            if (score < 0.72)
            {
                continue;
            }

            if (bestPerson is null || score > bestScore + 1e-9)
            {
                bestPerson = person;
                bestScore = score;
                tiedBestCount = 1;
            }
            else if (Math.Abs(score - bestScore) <= 1e-9)
            {
                tiedBestCount++;
            }
        }

        if (bestPerson is not null)
        {
            if (tiedBestCount > 1)
            {
                return new MatchCandidate(
                    bestPerson,
                    MatchConfidence.Possible,
                    bestScore,
                    $"Similarity score {bestScore:F2} ({tiedBestCount} roster candidates)",
                    NeedsReview: true,
                    ReviewReasonCode: MatchReviewReasons.AmbiguousPossibleMatch);
            }

            return new MatchCandidate(
                bestPerson,
                MatchConfidence.Possible,
                bestScore,
                $"Similarity score {bestScore:F2}",
                NeedsReview: true,
                ReviewReasonCode: MatchReviewReasons.PossibleMatch);
        }

        return MatchCandidate.Unmatched("No roster candidate exceeded threshold");
    }

    private static List<RosterPerson> FindEmailMatches(IReadOnlyList<RosterPerson> roster, string normalizedEmail)
    {
        var matches = new List<RosterPerson>();
        for (var i = 0; i < roster.Count; i++)
        {
            var person = roster[i];
            if (!string.IsNullOrWhiteSpace(person.Email)
                && string.Equals(person.Email.Trim(), normalizedEmail, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(person);
            }
        }

        return matches;
    }

    private static List<RosterPerson> FindExactNameMatches(IReadOnlyList<RosterPerson> roster, string normalizedName)
    {
        var matches = new List<RosterPerson>();
        for (var i = 0; i < roster.Count; i++)
        {
            var person = roster[i];
            if (string.Equals(person.NormalizedName, normalizedName, StringComparison.Ordinal))
            {
                matches.Add(person);
            }
        }

        return matches;
    }

    private static RosterPerson PickBestEmailCandidate(IReadOnlyList<RosterPerson> emailMatches, string normalizedName)
    {
        if (!string.IsNullOrEmpty(normalizedName))
        {
            RosterPerson? uniqueNameMatch = null;
            var nameMatchCount = 0;
            for (var i = 0; i < emailMatches.Count; i++)
            {
                var person = emailMatches[i];
                if (string.Equals(person.NormalizedName, normalizedName, StringComparison.Ordinal))
                {
                    uniqueNameMatch = person;
                    nameMatchCount++;
                }
            }

            if (nameMatchCount == 1 && uniqueNameMatch is not null)
            {
                return uniqueNameMatch;
            }
        }

        return emailMatches[0];
    }

    private static bool HasOtherPerson(IReadOnlyList<RosterPerson> candidates, string matchedPersonId)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!string.Equals(candidates[i].Id, matchedPersonId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEmailConflict(string? rosterEmail, string? normalizedParticipantEmail)
        => !string.IsNullOrWhiteSpace(normalizedParticipantEmail)
            && !string.IsNullOrWhiteSpace(rosterEmail)
            && !string.Equals(rosterEmail.Trim(), normalizedParticipantEmail, StringComparison.OrdinalIgnoreCase);

    private static double CalculateNameScore(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        if (left == right)
        {
            return 1;
        }

        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal))
        {
            // Very short Korean names are especially collision-prone (for example 김민 -> 김민수/김민지).
            // Do not auto-attach a participant to a two-character substring.
            return Math.Min(left.Length, right.Length) < 3 ? 0 : 0.8;
        }

        var distance = LevenshteinDistance(left, right);
        var maxLength = Math.Max(left.Length, right.Length);
        return maxLength == 0 ? 0 : 1 - (double)distance / maxLength;
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var matrix = new int[left.Length + 1, right.Length + 1];

        for (var i = 0; i <= left.Length; i++)
        {
            matrix[i, 0] = i;
        }

        for (var j = 0; j <= right.Length; j++)
        {
            matrix[0, j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + substitutionCost);
            }
        }

        return matrix[left.Length, right.Length];
    }
}
