namespace ZoomCheck.Core.Services;

using ZoomCheck.Core.Models;

/// <summary>
/// Strictly canonicalizes Korean display names that Zoom shows in reordered
/// "given-name surname" form (for example "영인 유" for roster person "유영인").
/// </summary>
/// <remarks>
/// The rules are deliberately narrow so that ordinary names, nicknames, device
/// suffixes and non-roster names are never rewritten:
/// <list type="bullet">
/// <item>the raw name must consist of exactly two tokens separated by exactly one ASCII space;</item>
/// <item>the first token must be two or more Hangul syllables (the given name);</item>
/// <item>the second token must be exactly one Hangul syllable (the surname);</item>
/// <item>the raw name must not already resolve to a roster person;</item>
/// <item>the reversed form (surname + given name) must match exactly one roster person.</item>
/// </list>
/// Anything else is left untouched.
/// </remarks>
public static class KoreanNameCanonicalizer
{
    /// <summary>
    /// Attempts to resolve <paramref name="rawName"/> to a roster person's canonical name.
    /// </summary>
    /// <returns><see langword="true"/> when a unique reordered roster match exists.</returns>
    public static bool TryCanonicalize(
        string? rawName,
        IReadOnlyList<RosterPerson>? roster,
        out string canonicalName,
        out RosterPerson? matchedPerson)
    {
        canonicalName = string.Empty;
        matchedPerson = null;

        if (roster is null || roster.Count == 0 || string.IsNullOrEmpty(rawName))
        {
            return false;
        }

        if (!TrySplitReorderedTokens(rawName, out var givenName, out var surname))
        {
            return false;
        }

        var normalizedRaw = NameNormalizer.Normalize(rawName);
        if (string.IsNullOrEmpty(normalizedRaw))
        {
            return false;
        }

        // Never rewrite a name that already resolves to somebody on the roster.
        if (roster.Any(person => string.Equals(person.NormalizedName, normalizedRaw, StringComparison.Ordinal)))
        {
            return false;
        }

        var reversed = surname + givenName;
        var normalizedReversed = NameNormalizer.Normalize(reversed);
        if (string.IsNullOrEmpty(normalizedReversed))
        {
            return false;
        }

        var matches = roster
            .Where(person => string.Equals(person.NormalizedName, normalizedReversed, StringComparison.Ordinal))
            .Take(2)
            .ToArray();

        if (matches.Length != 1)
        {
            // Zero matches means the name is not on the roster; more than one is ambiguous.
            return false;
        }

        matchedPerson = matches[0];
        canonicalName = string.IsNullOrWhiteSpace(matchedPerson.Name) ? reversed : matchedPerson.Name;
        return true;
    }

    /// <summary>
    /// Convenience overload returning the canonical name, or <see langword="null"/> when the
    /// strict reordered-name rules do not apply.
    /// </summary>
    public static string? Canonicalize(string? rawName, IReadOnlyList<RosterPerson>? roster)
        => TryCanonicalize(rawName, roster, out var canonical, out _) ? canonical : null;

    private static bool TrySplitReorderedTokens(string rawName, out string givenName, out string surname)
    {
        givenName = string.Empty;
        surname = string.Empty;

        // Exactly one ASCII space, and no other whitespace anywhere.
        var spaceIndex = rawName.IndexOf(' ');
        if (spaceIndex <= 0 || spaceIndex == rawName.Length - 1)
        {
            return false;
        }

        if (rawName.IndexOf(' ', spaceIndex + 1) >= 0)
        {
            return false;
        }

        for (var index = 0; index < rawName.Length; index++)
        {
            if (index == spaceIndex)
            {
                continue;
            }

            if (char.IsWhiteSpace(rawName[index]))
            {
                return false;
            }
        }

        var first = rawName[..spaceIndex];
        var second = rawName[(spaceIndex + 1)..];

        if (first.Length < 2 || second.Length != 1)
        {
            return false;
        }

        if (!IsHangulSyllableRun(first) || !IsHangulSyllableRun(second))
        {
            return false;
        }

        givenName = first;
        surname = second;
        return true;
    }

    private static bool IsHangulSyllableRun(string value)
    {
        foreach (var character in value)
        {
            if (character is < '\uAC00' or > '\uD7A3')
            {
                return false;
            }
        }

        return value.Length > 0;
    }
}
