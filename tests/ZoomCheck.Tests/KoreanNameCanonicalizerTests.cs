using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Tests;

public sealed class KoreanNameCanonicalizerTests
{
    private static RosterPerson Person(string id, string name)
        => new(
            Id: id,
            Sequence: id,
            Name: name,
            NormalizedName: NameNormalizer.Normalize(name),
            Email: string.Empty,
            Phone: string.Empty,
            Organization: string.Empty,
            Aliases: Array.Empty<string>());

    private static RosterPerson[] Roster() => new[]
    {
        Person("p1", "유영인"),
        Person("p2", "이순신")
    };

    [Fact]
    public void Canonicalize_ReordersGivenNameSurname_WhenUniqueRosterMatch()
    {
        Assert.True(KoreanNameCanonicalizer.TryCanonicalize("영인 유", Roster(), out var canonical, out var person));

        Assert.Equal("유영인", canonical);
        Assert.Equal("p1", person!.Id);
    }

    [Fact]
    public void Canonicalize_UsesRosterSpelling_ForCanonicalName()
    {
        var roster = new[] { Person("p1", "유영인") };

        Assert.Equal("유영인", KoreanNameCanonicalizer.Canonicalize("영인 유", roster));
    }

    [Theory]
    // Surname token must be exactly one Hangul syllable.
    [InlineData("영인 유씨")]
    // Given-name token must be two or more syllables.
    [InlineData("영 유")]
    // Exactly one ASCII space, nothing else.
    [InlineData("영인  유")]
    [InlineData("영인\t유")]
    [InlineData("영인 유 님")]
    [InlineData("영인유")]
    // Non-Hangul tokens are never reordered.
    [InlineData("Youngin Yu")]
    [InlineData("영인 Y")]
    [InlineData("영인 유1")]
    // Reversed form is not on the roster.
    [InlineData("민수 박")]
    public void Canonicalize_ReturnsNull_ForInputsOutsideStrictRule(string rawName)
    {
        Assert.Null(KoreanNameCanonicalizer.Canonicalize(rawName, Roster()));
    }

    [Fact]
    public void Canonicalize_ReturnsNull_WhenReversedFormIsAmbiguous()
    {
        var roster = new[]
        {
            Person("p1", "유영인"),
            Person("p2", "유영인")
        };

        Assert.Null(KoreanNameCanonicalizer.Canonicalize("영인 유", roster));
    }

    [Fact]
    public void Canonicalize_ReturnsNull_WhenRawNameAlreadyMatchesRoster()
    {
        // "순신 이" reversed is "이순신", but a roster person literally named "순신 이"
        // must win over the reordering rule.
        var roster = new[]
        {
            Person("p1", "순신 이"),
            Person("p2", "이순신")
        };

        Assert.Null(KoreanNameCanonicalizer.Canonicalize("순신 이", roster));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Canonicalize_ReturnsNull_ForEmptyInput(string? rawName)
    {
        Assert.Null(KoreanNameCanonicalizer.Canonicalize(rawName, Roster()));
    }

    [Fact]
    public void Canonicalize_ReturnsNull_ForEmptyRoster()
    {
        Assert.Null(KoreanNameCanonicalizer.Canonicalize("영인 유", Array.Empty<RosterPerson>()));
        Assert.Null(KoreanNameCanonicalizer.Canonicalize("영인 유", null));
    }
}
