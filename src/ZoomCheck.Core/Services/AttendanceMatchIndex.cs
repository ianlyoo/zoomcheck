using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;

namespace ZoomCheck.Core.Services;

/// <summary>
/// Pre-indexed view of a roster and alias map for repeated matching and canonicalization
/// within a snapshot or board projection. Preserves exact <see cref="AttendanceMatcher"/>
/// and <see cref="KoreanNameCanonicalizer"/> precedence and first-match semantics.
/// </summary>
public sealed class AttendanceMatchIndex
{
    private readonly IReadOnlyList<RosterPerson> _roster;
    private readonly IReadOnlyDictionary<string, string> _aliasMap;
    private readonly Dictionary<string, RosterPerson> _byEmail;
    private readonly Dictionary<string, RosterPerson> _byId;
    private readonly Dictionary<string, RosterPerson> _byNormalizedName;
    private readonly Dictionary<string, RosterPerson?> _uniqueByNormalizedName;

    public AttendanceMatchIndex(
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliasMap)
    {
        _roster = roster;
        _aliasMap = aliasMap;
        _byEmail = new Dictionary<string, RosterPerson>(roster.Count, StringComparer.OrdinalIgnoreCase);
        _byId = new Dictionary<string, RosterPerson>(roster.Count, StringComparer.Ordinal);
        _byNormalizedName = new Dictionary<string, RosterPerson>(roster.Count, StringComparer.Ordinal);
        _uniqueByNormalizedName = new Dictionary<string, RosterPerson?>(roster.Count, StringComparer.Ordinal);

        foreach (var person in roster)
        {
            if (!string.IsNullOrWhiteSpace(person.Email))
            {
                _byEmail.TryAdd(person.Email, person);
            }

            _byId.TryAdd(person.Id, person);

            if (!string.IsNullOrEmpty(person.NormalizedName))
            {
                _byNormalizedName.TryAdd(person.NormalizedName, person);
                if (!_uniqueByNormalizedName.TryAdd(person.NormalizedName, person))
                {
                    _uniqueByNormalizedName[person.NormalizedName] = null;
                }
            }
        }
    }

    public IReadOnlyList<RosterPerson> Roster => _roster;

    public IReadOnlyDictionary<string, string> AliasMap => _aliasMap;

    public bool TryGetPersonById(string personId, out RosterPerson? person)
        => _byId.TryGetValue(personId, out person);

    public MatchCandidate Match(string participantName, string? participantEmail)
    {
        var normalizedName = NameNormalizer.Normalize(participantName);
        var normalizedEmail = participantEmail?.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(normalizedEmail)
            && _byEmail.TryGetValue(normalizedEmail, out var emailMatch))
        {
            return new MatchCandidate(emailMatch, MatchConfidence.Verified, 1.0, "Email exact match");
        }

        if (_aliasMap.TryGetValue(normalizedName, out var aliasPersonId)
            && _byId.TryGetValue(aliasPersonId, out var aliasMatch))
        {
            return new MatchCandidate(aliasMatch, MatchConfidence.AliasVerified, 0.98, "Saved alias match");
        }

        if (_byNormalizedName.TryGetValue(normalizedName, out var nameMatch))
        {
            return new MatchCandidate(nameMatch, MatchConfidence.NameOnly, 0.9, "Normalized name exact match");
        }

        return AttendanceMatcher.FindBestPossibleMatch(_roster, normalizedName);
    }

    public MatchCandidate MatchParticipant(string rawName, string displayName, string? participantEmail)
    {
        var rawCandidate = Match(rawName, participantEmail);
        if (rawCandidate.Confidence is MatchConfidence.Verified or MatchConfidence.AliasVerified)
        {
            return rawCandidate;
        }

        return string.Equals(rawName, displayName, StringComparison.Ordinal)
            ? rawCandidate
            : Match(displayName, participantEmail);
    }

    public string? ResolveCanonicalName(string rawName, string? participantEmail)
    {
        if (!TryCanonicalizeKoreanName(rawName, out var canonicalName, out var canonicalPerson))
        {
            return null;
        }

        // Explicit identity evidence must win over an automatic display-name rewrite. If an
        // email or operator-saved alias says this is a different roster person, leave the raw
        // name untouched instead of presenting a misleading canonical name.
        var rawCandidate = Match(rawName, participantEmail);
        var hasExplicitRawMatch = rawCandidate.Confidence is MatchConfidence.Verified or MatchConfidence.AliasVerified;
        if (hasExplicitRawMatch
            && rawCandidate.Person is not null
            && !string.Equals(rawCandidate.Person.Id, canonicalPerson!.Id, StringComparison.Ordinal))
        {
            return null;
        }

        return canonicalName;
    }

    public bool TryCanonicalizeKoreanName(
        string? rawName,
        out string canonicalName,
        out RosterPerson? matchedPerson)
    {
        canonicalName = string.Empty;
        matchedPerson = null;

        if (_roster.Count == 0 || string.IsNullOrEmpty(rawName))
        {
            return false;
        }

        if (!KoreanNameCanonicalizer.TrySplitReorderedTokens(rawName, out var givenName, out var surname))
        {
            return false;
        }

        var normalizedRaw = NameNormalizer.Normalize(rawName);
        if (string.IsNullOrEmpty(normalizedRaw) || _byNormalizedName.ContainsKey(normalizedRaw))
        {
            return false;
        }

        var reversed = surname + givenName;
        var normalizedReversed = NameNormalizer.Normalize(reversed);
        if (string.IsNullOrEmpty(normalizedReversed)
            || !_uniqueByNormalizedName.TryGetValue(normalizedReversed, out var uniquePerson)
            || uniquePerson is null)
        {
            return false;
        }

        matchedPerson = uniquePerson;
        canonicalName = string.IsNullOrWhiteSpace(matchedPerson.Name) ? reversed : matchedPerson.Name;
        return true;
    }
}
