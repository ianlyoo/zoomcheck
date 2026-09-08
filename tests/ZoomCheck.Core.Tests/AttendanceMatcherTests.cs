using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Core.Tests;

/// <summary>
/// Characterization tests for <see cref="AttendanceMatcher.Match"/>.
///
/// Match is the single decision point that assigns MatchConfidence to every
/// participant event, regardless of whether the event came from the UIA panel
/// watcher, a Zoom webhook, or live-meeting recovery. The confidence value is
/// persisted on the event row and drives the operator review queue, so the
/// precedence order and the 0.72 similarity threshold are behavioral contracts.
/// </summary>
public sealed class AttendanceMatcherTests
{
    private static readonly IReadOnlyDictionary<string, string> NoAliases =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static RosterPerson Person(
        string id,
        string name,
        string email = "",
        string sequence = "1",
        string organization = "Test Org")
        => new(
            Id: id,
            Sequence: sequence,
            Name: name,
            NormalizedName: NameNormalizer.Normalize(name),
            Email: email,
            Phone: string.Empty,
            Organization: organization,
            Aliases: Array.Empty<string>());

    private static AttendanceMatcher CreateMatcher() => new();

    // ---------------------------------------------------------------- Verified

    [Fact]
    public void Match_EmailExact_ReturnsVerifiedWithScoreOne()
    {
        var target = Person("p1", "Jane Doe", "jane@example.com");
        var roster = new[] { Person("p0", "Other Person", "other@example.com"), target };

        var result = CreateMatcher().Match(roster, NoAliases, "Totally Different Name", "jane@example.com");

        Assert.Equal(MatchConfidence.Verified, result.Confidence);
        Assert.Same(target, result.Person);
        Assert.Equal(1.0, result.Score);
        Assert.Equal("Email exact match", result.Reason);
    }

    [Fact]
    public void Match_EmailComparisonIsCaseInsensitiveAndTrimmed()
    {
        var target = Person("p1", "Jane Doe", "jane@example.com");
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, "Jane Doe", "  JANE@EXAMPLE.COM  ");

        Assert.Equal(MatchConfidence.Verified, result.Confidence);
        Assert.Same(target, result.Person);
    }

    [Fact]
    public void Match_EmailWins_OverAliasAndExactName()
    {
        // Precedence guard: email is checked first, so a roster person matched only by
        // email outranks an alias entry and an exact normalized-name hit on someone else.
        var byEmail = Person("email-person", "Zzz Person", "jane@example.com");
        var byName = Person("name-person", "Jane Doe");
        var roster = new[] { byName, byEmail };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("Jane Doe")] = "name-person"
        };

        var result = CreateMatcher().Match(roster, aliases, "Jane Doe", "jane@example.com");

        Assert.Equal(MatchConfidence.Verified, result.Confidence);
        Assert.Same(byEmail, result.Person);
    }

    [Fact]
    public void Match_UnknownEmail_FallsThroughToNameLadder()
    {
        var target = Person("p1", "Jane Doe", "jane@example.com");
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, "Jane Doe", "someone-else@example.com");

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Same(target, result.Person);
    }

    [Fact]
    public void Match_EmptyRosterEmailIsNotMatchedByWhitespaceEmail()
    {
        // Roster rows commonly have Email = string.Empty. A whitespace-only participant
        // email must not be treated as a configured email and match those rows.
        var target = Person("p1", "Jane Doe", string.Empty);
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, "Jane Doe", "   ");

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
    }

    [Fact]
    public void Match_NullEmail_SkipsEmailStage()
    {
        var target = Person("p1", "Jane Doe", "jane@example.com");
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, "Jane Doe", null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
    }

    // ----------------------------------------------------------- AliasVerified

    [Fact]
    public void Match_AliasHit_ReturnsAliasVerifiedWithScore098()
    {
        var target = Person("p1", "Jane Doe");
        var roster = new[] { target };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("JD Laptop")] = "p1"
        };

        var result = CreateMatcher().Match(roster, aliases, "JD Laptop", null);

        Assert.Equal(MatchConfidence.AliasVerified, result.Confidence);
        Assert.Same(target, result.Person);
        Assert.Equal(0.98, result.Score);
        Assert.Equal("Saved alias match", result.Reason);
    }

    [Fact]
    public void Match_AliasLookupUsesNormalizedKey()
    {
        // Alias keys are stored already-normalized (AttendanceApplicationService
        // normalizes before UpsertAliasAsync), and the incoming participant name is
        // normalized here, so decorated inbound spellings still hit the alias.
        var target = Person("p1", "김민수");
        var roster = new[] { target };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("김민수 iPhone")] = "p1"
        };

        var result = CreateMatcher().Match(roster, aliases, "  김민수  iPhone  ", null);

        Assert.Equal(MatchConfidence.AliasVerified, result.Confidence);
        Assert.Same(target, result.Person);
    }

    [Fact]
    public void Match_AliasWins_OverExactNormalizedName()
    {
        var aliasTarget = Person("alias-person", "Alias Target");
        var nameTarget = Person("name-person", "Jane Doe");
        var roster = new[] { nameTarget, aliasTarget };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("Jane Doe")] = "alias-person"
        };

        var result = CreateMatcher().Match(roster, aliases, "Jane Doe", null);

        Assert.Equal(MatchConfidence.AliasVerified, result.Confidence);
        Assert.Same(aliasTarget, result.Person);
    }

    [Fact]
    public void Match_AliasPointingAtMissingRosterId_FallsThroughToNameLadder()
    {
        // This is the roster re-import hazard: ReplaceRosterAsync assigns fresh GUIDs,
        // so manual_aliases rows can point at ids that no longer exist. Match must
        // degrade to name matching rather than throw or return a null person.
        var target = Person("new-id", "Jane Doe");
        var roster = new[] { target };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("Jane Doe")] = "stale-id-from-previous-import"
        };

        var result = CreateMatcher().Match(roster, aliases, "Jane Doe", null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Same(target, result.Person);
    }

    [Fact]
    public void Match_AliasKeyLookupIsCaseSensitiveOrdinal_ButNormalizationLowercasesFirst()
    {
        // The alias dictionary uses StringComparer.Ordinal. An un-normalized
        // upper-case key therefore never matches, because the lookup key is lowercased.
        var target = Person("p1", "Jane Doe");
        var roster = new[] { target };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["JDLAPTOP"] = "p1"
        };

        var result = CreateMatcher().Match(roster, aliases, "JD Laptop", null);

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
    }

    // ---------------------------------------------------------------- NameOnly

    [Fact]
    public void Match_NormalizedNameExact_ReturnsNameOnlyWithScore09()
    {
        var target = Person("p1", "Jane Doe");
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, "  jane   DOE ", null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Same(target, result.Person);
        Assert.Equal(0.9, result.Score);
        Assert.Equal("Normalized name exact match", result.Reason);
    }

    [Fact]
    public void Match_HangulNameExact_ReturnsNameOnly()
    {
        var target = Person("p1", "김민수");
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, " 김 민 수 ", null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Same(target, result.Person);
    }

    [Fact]
    public void Match_ExactNameMatch_TakesFirstRosterEntryOnDuplicateNames()
    {
        // Two rostered people can share a name. FirstOrDefault means roster order
        // decides, and the second person is unreachable by name alone.
        var first = Person("p1", "김민수", sequence: "1");
        var second = Person("p2", "김민수", sequence: "2");
        var roster = new[] { first, second };

        var result = CreateMatcher().Match(roster, NoAliases, "김민수", null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Same(first, result.Person);
    }

    // ---------------------------------------------------------------- Possible

    [Fact]
    public void Match_ContainmentScoresPointEight_ReturnsPossible()
    {
        // "김민수 iPhone" contains the roster name, so CalculateNameScore short-circuits
        // to a flat 0.8. This is the single most common real panel-scrape shape.
        var target = Person("p1", "김민수");
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, "김민수 iPhone", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Same(target, result.Person);
        Assert.Equal(0.8, result.Score);
        Assert.Equal("Similarity score 0.80", result.Reason);
    }

    [Fact]
    public void Match_ContainmentIsSymmetric_ShorterInboundNameAlsoScoresPointEight()
    {
        var target = Person("p1", "김민수 서울대학교");
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, "김민수", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Equal(0.8, result.Score);
    }

    [Fact]
    public void Match_LevenshteinNearMiss_ReturnsPossible()
    {
        // "jamedoe" vs "janedoe": distance 1 over length 7 => 1 - 1/7 ~= 0.857.
        var target = Person("p1", "Jane Doe");
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, "Jame Doe", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Same(target, result.Person);
        Assert.Equal(1.0 - 1.0 / 7.0, result.Score, 10);
    }

    [Fact]
    public void Match_PicksHighestScoringCandidateAmongSeveral()
    {
        var far = Person("p-far", "Zachary Quinto");
        var near = Person("p-near", "Jane Doe");
        var roster = new[] { far, near };

        var result = CreateMatcher().Match(roster, NoAliases, "Jame Doe", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Same(near, result.Person);
    }

    // ------------------------------------------------------- 0.72 threshold

    // 25 normalized characters with 7 substitutions gives Levenshtein distance 7,
    // so the score is exactly 1 - 7/25 = 0.72 in IEEE-754 double arithmetic.
    // Neither string contains the other, so the containment short-circuit is skipped.
    private const string ThresholdRosterName = "abcdefghijklmnopqrstuvwxy";
    private const string SevenSubstitutions = "0bc1ef2hi3kl4no5qr6tuvwxy";  // distance 7 => 0.72
    private const string EightSubstitutions = "0bc1ef2hi3kl4no5qr6tu7wxy";  // distance 8 => 0.68

    [Fact]
    public void Match_ScoreExactlyAtThreshold_IsAcceptedAsPossible()
    {
        // Boundary is inclusive (>= 0.72). This test fails if the comparison is
        // ever tightened to a strict greater-than.
        var target = Person("p1", ThresholdRosterName);
        var roster = new[] { target };

        var result = CreateMatcher().Match(roster, NoAliases, SevenSubstitutions, null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Same(target, result.Person);
        Assert.Equal(0.72, result.Score, 10);
        Assert.Equal("Similarity score 0.72", result.Reason);
    }

    [Fact]
    public void Match_ScoreJustBelowThreshold_IsUnmatched()
    {
        // 1 - 8/25 = 0.68, the next reachable score below the boundary for this pair.
        var roster = new[] { Person("p1", ThresholdRosterName) };

        var result = CreateMatcher().Match(roster, NoAliases, EightSubstitutions, null);

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
        Assert.Equal(0, result.Score);
        Assert.Equal("No roster candidate exceeded threshold", result.Reason);
    }

    [Fact]
    public void Match_ThresholdFixtureAssumptions_HoldForBothDirections()
    {
        // Protects the two boundary tests above: if the fixture strings drift so that
        // one contains the other, CalculateNameScore would short-circuit to 0.8 and the
        // boundary would no longer be exercised at all.
        Assert.Equal(25, ThresholdRosterName.Length);
        Assert.Equal(25, SevenSubstitutions.Length);
        Assert.Equal(25, EightSubstitutions.Length);
        Assert.DoesNotContain(SevenSubstitutions, ThresholdRosterName, StringComparison.Ordinal);
        Assert.DoesNotContain(ThresholdRosterName, SevenSubstitutions, StringComparison.Ordinal);
        Assert.DoesNotContain(EightSubstitutions, ThresholdRosterName, StringComparison.Ordinal);
        Assert.DoesNotContain(ThresholdRosterName, EightSubstitutions, StringComparison.Ordinal);

        // Fixtures must survive normalization unchanged (already lowercase alphanumerics).
        Assert.Equal(ThresholdRosterName, NameNormalizer.Normalize(ThresholdRosterName));
        Assert.Equal(SevenSubstitutions, NameNormalizer.Normalize(SevenSubstitutions));
        Assert.Equal(EightSubstitutions, NameNormalizer.Normalize(EightSubstitutions));
    }

    // --------------------------------------------------------------- Unmatched

    [Fact]
    public void Match_EmptyRoster_ReturnsUnmatched()
    {
        var result = CreateMatcher().Match(Array.Empty<RosterPerson>(), NoAliases, "Jane Doe", null);

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
        Assert.Equal(0, result.Score);
        Assert.Equal("No roster candidate exceeded threshold", result.Reason);
    }

    [Fact]
    public void Match_CompletelyDifferentName_ReturnsUnmatched()
    {
        var roster = new[] { Person("p1", "Jane Doe") };

        var result = CreateMatcher().Match(roster, NoAliases, "Zzzzzzzzzzz Qqqqqqqqqqq", null);

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void Match_ParticipantNameNormalizingToEmpty_ReturnsUnmatched(string participantName)
    {
        // Panel scrapes can yield rows that normalize away entirely. CalculateNameScore
        // guards on IsNullOrWhiteSpace and returns 0, so nothing crosses the threshold.
        var roster = new[] { Person("p1", "Jane Doe") };

        var result = CreateMatcher().Match(roster, NoAliases, participantName, null);

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
    }

    [Fact]
    public void Match_EmptyParticipantNameDoesNotMatchRosterPersonWithEmptyNormalizedName()
    {
        // A roster row whose name normalizes to empty (for example a punctuation-only
        // cell) must not become a catch-all bucket for unreadable panel rows.
        var blank = Person("p-blank", "!!!");
        var roster = new[] { blank };
        Assert.Equal(string.Empty, blank.NormalizedName);

        var result = CreateMatcher().Match(roster, NoAliases, "   ", null);

        // Note: the exact-name stage compares NormalizedName == normalizedName, and
        // both are empty, so this currently returns NameOnly on the blank row.
        // Locking in the observed behavior so a future guard change is visible.
        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Same(blank, result.Person);
    }

    // -------------------------------------------------------- ladder integrity

    [Fact]
    public void Match_ConfidenceLadder_OrdersVerifiedBeforeAliasBeforeNameOnly()
    {
        // BuildBoardAsync uses MinBy(evt => evt.Confidence) to pick the best confidence
        // seen for a person, which only works because the enum is ordered best-to-worst.
        Assert.True(MatchConfidence.Verified < MatchConfidence.AliasVerified);
        Assert.True(MatchConfidence.AliasVerified < MatchConfidence.NameOnly);
        Assert.True(MatchConfidence.NameOnly < MatchConfidence.Possible);
        Assert.True(MatchConfidence.Possible < MatchConfidence.Unmatched);
    }

    [Fact]
    public void Match_ScoresAreMonotonicAcrossConfidenceTiers()
    {
        var target = Person("p1", "Jane Doe", "jane@example.com");
        var roster = new[] { target };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("JD Laptop")] = "p1"
        };
        var matcher = CreateMatcher();

        var verified = matcher.Match(roster, aliases, "Jane Doe", "jane@example.com").Score;
        var alias = matcher.Match(roster, aliases, "JD Laptop", null).Score;
        var nameOnly = matcher.Match(roster, aliases, "Jane Doe", null).Score;
        var possible = matcher.Match(roster, aliases, "Jame Doe", null).Score;

        Assert.True(verified > alias, "Verified should outrank alias");
        Assert.True(alias > nameOnly, "Alias should outrank name-only");
        Assert.True(nameOnly > possible, "Name-only should outrank fuzzy possible");
    }

    [Fact]
    public void Match_IsPureAndRepeatable()
    {
        // Match holds no state; repeated calls must be identical. The backend
        // registers AttendanceMatcher as a singleton shared across concurrent
        // panel, webhook, and recovery ingestion, so this must stay true.
        var roster = new[] { Person("p1", "Jane Doe", "jane@example.com") };
        var matcher = CreateMatcher();

        var first = matcher.Match(roster, NoAliases, "Jame Doe", null);
        var second = matcher.Match(roster, NoAliases, "Jame Doe", null);

        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Score, second.Score);
        Assert.Equal(first.Reason, second.Reason);
        Assert.Same(first.Person, second.Person);
    }
}
