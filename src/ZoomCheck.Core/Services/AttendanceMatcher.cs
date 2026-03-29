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

        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            var emailMatch = roster.FirstOrDefault(person => string.Equals(person.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase));
            if (emailMatch is not null)
            {
                return new MatchCandidate(emailMatch, MatchConfidence.Verified, 1.0, "Email exact match");
            }
        }

        if (aliasMap.TryGetValue(normalizedName, out var aliasPersonId))
        {
            var aliasMatch = roster.FirstOrDefault(person => person.Id == aliasPersonId);
            if (aliasMatch is not null)
            {
                return new MatchCandidate(aliasMatch, MatchConfidence.AliasVerified, 0.98, "Saved alias match");
            }
        }

        var nameMatch = roster.FirstOrDefault(person => person.NormalizedName == normalizedName);
        if (nameMatch is not null)
        {
            return new MatchCandidate(nameMatch, MatchConfidence.NameOnly, 0.9, "Normalized name exact match");
        }

        var bestPossible = roster
            .Select(person => new { Person = person, Score = CalculateNameScore(normalizedName, person.NormalizedName) })
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();

        if (bestPossible is not null && bestPossible.Score >= 0.72)
        {
            return new MatchCandidate(bestPossible.Person, MatchConfidence.Possible, bestPossible.Score, $"Similarity score {bestPossible.Score:F2}");
        }

        return MatchCandidate.Unmatched("No roster candidate exceeded threshold");
    }

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
            return 0.8;
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
