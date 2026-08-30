using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Tests;

public class AttendanceMatcherTests
{
    private readonly AttendanceMatcher _matcher = new();

    private static RosterPerson Person(
        string id,
        string name,
        string email = "",
        params string[] aliases)
        => new(
            Id: id,
            Sequence: id,
            Name: name,
            NormalizedName: NameNormalizer.Normalize(name),
            Email: email,
            Phone: string.Empty,
            Organization: string.Empty,
            Aliases: aliases);

    private static Dictionary<string, string> NoAliases() => new(StringComparer.Ordinal);

    [Fact]
    public void Match_ReturnsVerified_OnEmailExactMatch()
    {
        var roster = new[] { Person("p1", "김영인", "youngin@example.com") };

        var result = _matcher.Match(roster, NoAliases(), "완전 다른 이름", "youngin@example.com");

        Assert.Equal(MatchConfidence.Verified, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
        Assert.Equal(1.0, result.Score);
    }

    [Theory]
    [InlineData("YOUNGIN@EXAMPLE.COM")]
    [InlineData("  youngin@example.com  ")]
    public void Match_ReturnsVerified_ForCaseAndWhitespaceVariantEmails(string participantEmail)
    {
        var roster = new[] { Person("p1", "김영인", "youngin@example.com") };

        var result = _matcher.Match(roster, NoAliases(), "unknown", participantEmail);

        Assert.Equal(MatchConfidence.Verified, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
    }

    [Fact]
    public void Match_EmailWins_OverNormalizedNameMatchOnAnotherPerson()
    {
        var roster = new[]
        {
            Person("p1", "김영인", "youngin@example.com"),
            Person("p2", "이순신", "sunshin@example.com")
        };

        var result = _matcher.Match(roster, NoAliases(), "이순신", "youngin@example.com");

        Assert.Equal(MatchConfidence.Verified, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
    }

    [Fact]
    public void Match_FallsBackToName_WhenEmailIsUnknown()
    {
        var roster = new[] { Person("p1", "김영인", "youngin@example.com") };

        var result = _matcher.Match(roster, NoAliases(), "김 영 인", "stranger@example.com");

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
    }

    [Theory]
    [InlineData("김영인(교사)")]
    [InlineData("김 영 인 (교사)")]
    [InlineData(" 김영인 [교사] ")]
    public void Match_ReturnsNameOnly_ForSpacingAndPunctuationVariantsOfSameName(string participantName)
    {
        var roster = new[] { Person("p1", "김 영 인 (교사)") };

        var result = _matcher.Match(roster, NoAliases(), participantName, null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
    }

    [Fact]
    public void Match_ReturnsPossible_WhenRosterNameCarriesExtraSuffix()
    {
        // Roster stores "김영인교사"; the participant typed only "김영인", a substring => Possible, not exact.
        var roster = new[] { Person("p1", "김 영 인 (교사)") };

        var result = _matcher.Match(roster, NoAliases(), "김영인", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Equal(0.8, result.Score);
        Assert.Equal("p1", result.Person!.Id);
    }

    [Fact]
    public void Match_ReturnsNameOnly_WhenNormalizedNamesAreExactlyEqual()
    {
        var roster = new[] { Person("p1", "김영인") };

        var result = _matcher.Match(roster, NoAliases(), " 김 영 인 ", null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Equal(0.9, result.Score);
        Assert.Equal("Normalized name exact match", result.Reason);
    }

    [Fact]
    public void Match_ReturnsAliasVerified_WhenAliasMapHasNormalizedName()
    {
        var roster = new[] { Person("p1", "김영인"), Person("p2", "이순신") };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("영인쌤")] = "p1"
        };

        var result = _matcher.Match(roster, aliases, "영 인 쌤", null);

        Assert.Equal(MatchConfidence.AliasVerified, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
        Assert.Equal(0.98, result.Score);
    }

    [Fact]
    public void Match_AliasWins_OverNormalizedNameOfAnotherPerson()
    {
        var roster = new[] { Person("p1", "김영인"), Person("p2", "이순신") };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("이순신")] = "p1"
        };

        var result = _matcher.Match(roster, aliases, "이순신", null);

        Assert.Equal(MatchConfidence.AliasVerified, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
    }

    [Fact]
    public void Match_IgnoresAlias_WhenTargetPersonIsNotInRoster()
    {
        var roster = new[] { Person("p1", "김영인") };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("김영인")] = "missing-person"
        };

        var result = _matcher.Match(roster, aliases, "김영인", null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
    }

    [Fact]
    public void Match_ReturnsPossible_WhenParticipantNameContainsRosterName()
    {
        var roster = new[] { Person("p1", "김영인") };

        var result = _matcher.Match(roster, NoAliases(), "김영인 선생님", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Equal(0.8, result.Score);
        Assert.Equal("p1", result.Person!.Id);
        Assert.Contains("Similarity score", result.Reason);
    }

    [Fact]
    public void Match_ReturnsPossible_ForSmallTypoAboveThreshold()
    {
        var roster = new[] { Person("p1", "kimm") };

        // Levenshtein distance 1 over length 4 => 0.75, above the 0.72 threshold.
        var result = _matcher.Match(roster, NoAliases(), "kims", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Equal(0.75, result.Score, 3);
        Assert.Equal("p1", result.Person!.Id);
    }

    [Fact]
    public void Match_ReturnsUnmatched_ForSmallTypoBelowThreshold()
    {
        var roster = new[] { Person("p1", "kim") };

        // Levenshtein distance 1 over length 3 => ~0.67, below the 0.72 threshold.
        var result = _matcher.Match(roster, NoAliases(), "kin", null);

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
        Assert.Equal(0, result.Score);
        Assert.Equal("No roster candidate exceeded threshold", result.Reason);
    }

    [Fact]
    public void Match_ReturnsUnmatched_ForCompletelyDifferentKoreanName()
    {
        var roster = new[] { Person("p1", "김영인") };

        var result = _matcher.Match(roster, NoAliases(), "박철수", null);

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_ReturnsUnmatched_ForBlankParticipantNameWithoutEmail(string? participantName)
    {
        var roster = new[] { Person("p1", "김영인") };

        var result = _matcher.Match(roster, NoAliases(), participantName!, null);

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
    }

    [Fact]
    public void Match_ReturnsUnmatched_ForEmptyRoster()
    {
        var result = _matcher.Match(Array.Empty<RosterPerson>(), NoAliases(), "김영인", "youngin@example.com");

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
    }

    [Fact]
    public void Match_ReturnsUnmatched_WhenRosterEmailsAreEmptyAndParticipantEmailIsBlank()
    {
        var roster = new[] { Person("p1", "김영인") };

        // A blank participant email must not match roster rows that have no email.
        var result = _matcher.Match(roster, NoAliases(), "박철수", "   ");

        Assert.Equal(MatchConfidence.Unmatched, result.Confidence);
        Assert.Null(result.Person);
    }

    [Fact]
    public void Match_WithDuplicateNormalizedNames_IsAmbiguousAndResolvesToFirstRosterEntry()
    {
        // Two homonyms: the matcher reports NameOnly (not Verified) and picks the first entry,
        // which is the signal the UI uses to ask a human to disambiguate via alias/email.
        var roster = new[]
        {
            Person("p1", "김영인"),
            Person("p2", "김 영 인")
        };

        var result = _matcher.Match(roster, NoAliases(), "김영인", null);

        Assert.Equal(MatchConfidence.NameOnly, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
        Assert.True(result.Score < 1.0);
    }

    [Fact]
    public void Match_AmbiguousHomonyms_AreDisambiguatedByAlias()
    {
        var roster = new[]
        {
            Person("p1", "김영인"),
            Person("p2", "김영인")
        };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("김영인")] = "p2"
        };

        var result = _matcher.Match(roster, aliases, "김영인", null);

        Assert.Equal(MatchConfidence.AliasVerified, result.Confidence);
        Assert.Equal("p2", result.Person!.Id);
    }

    [Fact]
    public void Match_PicksHighestScoringCandidate_AmongSeveralPossibleOnes()
    {
        var roster = new[]
        {
            Person("p1", "박철수"),
            Person("p2", "kimyoungin")
        };

        var result = _matcher.Match(roster, NoAliases(), "kimyoungim", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Equal("p2", result.Person!.Id);
    }

    [Fact]
    public void Match_SubstringCandidateCanLoseToHigherLevenshteinCandidate()
    {
        // Documents a scoring quirk: substring containment is a flat 0.8, so a near-miss
        // scored by edit distance (2/11 => ~0.82) outranks an actual prefix containment.
        var roster = new[]
        {
            Person("p1", "kimyoungil"),
            Person("p2", "kimyoungin")
        };

        var result = _matcher.Match(roster, NoAliases(), "kimyounginn", null);

        Assert.Equal(MatchConfidence.Possible, result.Confidence);
        Assert.Equal("p1", result.Person!.Id);
        Assert.True(result.Score > 0.8, "edit-distance candidate should outscore the flat 0.8 substring score");
    }

    [Fact]
    public void Match_ConfidenceOrdering_ReflectsPrecedence()
    {
        Assert.True(MatchConfidence.Verified < MatchConfidence.AliasVerified);
        Assert.True(MatchConfidence.AliasVerified < MatchConfidence.NameOnly);
        Assert.True(MatchConfidence.NameOnly < MatchConfidence.Possible);
        Assert.True(MatchConfidence.Possible < MatchConfidence.Unmatched);
    }
}
