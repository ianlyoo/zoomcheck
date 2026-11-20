using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;

namespace ZoomCheck.Core.Services;

/// <summary>
/// Pre-indexed view of a roster and alias map for repeated matching and canonicalization
/// within a snapshot or board projection. Preserves exact <see cref="AttendanceMatcher"/>
/// and <see cref="KoreanNameCanonicalizer"/> precedence, ambiguity detection, and review signals.
/// </summary>
public sealed class AttendanceMatchIndex
{
    private readonly IReadOnlyList<RosterPerson> _roster;
    private readonly IReadOnlyDictionary<string, string> _aliasMap;
    private readonly Dictionary<string, List<RosterPerson>> _byEmail;
    private readonly Dictionary<string, RosterPerson> _byId;
    private readonly Dictionary<string, List<RosterPerson>> _byNormalizedName;

    public AttendanceMatchIndex(
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliasMap)
    {
        _roster = roster;
        _aliasMap = aliasMap;
        _byEmail = new Dictionary<string, List<RosterPerson>>(StringComparer.OrdinalIgnoreCase);
        _byId = new Dictionary<string, RosterPerson>(roster.Count, StringComparer.Ordinal);
        _byNormalizedName = new Dictionary<string, List<RosterPerson>>(roster.Count, StringComparer.Ordinal);

        foreach (var person in roster)
        {
            var trimmedEmail = person.Email?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedEmail))
            {
                if (!_byEmail.TryGetValue(trimmedEmail, out var emailMatches))
                {
                    emailMatches = new List<RosterPerson>(1);
                    _byEmail[trimmedEmail] = emailMatches;
                }
                emailMatches.Add(person);
            }

            _byId.TryAdd(person.Id, person);

            if (!_byNormalizedName.TryGetValue(person.NormalizedName, out var nameMatches))
            {
                nameMatches = new List<RosterPerson>(1);
                _byNormalizedName[person.NormalizedName] = nameMatches;
            }
            nameMatches.Add(person);
        }
    }

    public IReadOnlyList<RosterPerson> Roster => _roster;

    public IReadOnlyDictionary<string, string> AliasMap => _aliasMap;

    public bool TryGetPersonById(string personId, out RosterPerson? person)
        => _byId.TryGetValue(personId, out person);

    public bool HasDuplicateRosterName(string normalizedName)
        => !string.IsNullOrEmpty(normalizedName)
            && _byNormalizedName.TryGetValue(normalizedName, out var matches)
            && matches.Count > 1;

    public MatchCandidate Match(string participantName, string? participantEmail)
    {
        var normalizedName = NameNormalizer.Normalize(participantName);
        var normalizedEmail = participantEmail?.Trim().ToLowerInvariant();

        IReadOnlyList<RosterPerson>? emailMatches = null;
        if (!string.IsNullOrWhiteSpace(normalizedEmail)
            && _byEmail.TryGetValue(normalizedEmail, out var emailList))
        {
            emailMatches = emailList;
        }

        RosterPerson? aliasPerson = null;
        if (_aliasMap.TryGetValue(normalizedName, out var aliasPersonId))
        {
            _byId.TryGetValue(aliasPersonId, out aliasPerson);
        }

        IReadOnlyList<RosterPerson> exactNameMatches = _byNormalizedName.TryGetValue(normalizedName, out var nameList)
            ? nameList
            : Array.Empty<RosterPerson>();

        return AttendanceMatcher.MatchResolved(
            _roster,
            normalizedName,
            normalizedEmail,
            emailMatches,
            aliasPerson,
            exactNameMatches);
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
            || !_byNormalizedName.TryGetValue(normalizedReversed, out var matches)
            || matches.Count != 1)
        {
            return false;
        }

        matchedPerson = matches[0];
        canonicalName = string.IsNullOrWhiteSpace(matchedPerson.Name) ? reversed : matchedPerson.Name;
        return true;
    }
}
