using ZoomCheck.Core.Services;

namespace ZoomCheck.Core.Tests;

/// <summary>
/// Characterization tests for <see cref="NameNormalizer.Normalize"/>.
///
/// These lock in current behavior rather than asserting an ideal. Every
/// identity key in ZoomCheck derives from this method: roster_people.normalized_name,
/// manual_aliases.alias_key, participant_events.normalized_participant_name, and the
/// panel watcher's in-memory join/leave dictionary. Changing Normalize silently
/// invalidates all previously persisted keys, so a deliberate break here should be a
/// visible test failure, not a quiet data migration problem.
/// </summary>
public sealed class NameNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData("\t \r\n ")]
    public void Normalize_NullOrWhitespace_ReturnsEmptyString(string? input)
    {
        // IsNullOrWhiteSpace short-circuits before any allocation.
        Assert.Equal(string.Empty, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_LowercasesAsciiLetters()
    {
        Assert.Equal("janedoe", NameNormalizer.Normalize("Jane Doe"));
    }

    [Theory]
    [InlineData("Jane Doe", "janedoe")]
    [InlineData("  Jane   Doe  ", "janedoe")]
    [InlineData("Jane\tDoe", "janedoe")]
    [InlineData("Jane\r\nDoe", "janedoe")]
    [InlineData("J a n e", "jane")]
    public void Normalize_RemovesAllInteriorAndOuterWhitespace(string input, string expected)
    {
        // Space is dropped by the SpaceSeparator branch; tab/CR/LF are dropped because
        // they are Control category and therefore fail the IsLetterOrDigit filter.
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("Jane-Doe", "janedoe")]
    [InlineData("Jane.Doe", "janedoe")]
    [InlineData("O'Brien", "obrien")]
    [InlineData("Jane (Host)", "janehost")]
    [InlineData("Jane_Doe", "janedoe")]
    [InlineData("Jane@Doe", "janedoe")]
    [InlineData("[Jane] {Doe}", "janedoe")]
    [InlineData("Jane / Doe", "janedoe")]
    [InlineData("!@#$%^&*()", "")]
    public void Normalize_StripsPunctuationAndSymbols(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_UnderscoreIsStripped_BecauseItIsConnectorPunctuation()
    {
        // Guards a subtle trap: '_' is a valid C# identifier char but
        // char.IsLetterOrDigit('_') is false, so it is removed.
        Assert.Equal("ab", NameNormalizer.Normalize("a_b"));
    }

    [Theory]
    [InlineData("Jane2", "jane2")]
    [InlineData("010-1234-5678", "01012345678")]
    [InlineData("Room 101", "room101")]
    public void Normalize_PreservesDigits(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("김민수", "김민수")]
    [InlineData(" 김민수 ", "김민수")]
    [InlineData("김 민 수", "김민수")]
    [InlineData("김민수 iPhone", "김민수iphone")]
    [InlineData("김민수(서울대)", "김민수서울대")]
    public void Normalize_PreservesPrecomposedHangulSyllables(string input, string expected)
    {
        // U+AC00..U+D7AF block. Mixed Hangul + Latin is the common Zoom display-name
        // shape ("김민수 iPhone"), so it must collapse to a stable key.
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_ComposesDecomposedHangulJamoIntoSyllables()
    {
        // NFKC composes conjoining jamo (U+1100 block) into precomposed syllables,
        // so the decomposed and precomposed spellings of the same name agree.
        // This is what makes IME- and macOS-originated names match roster entries.
        const string decomposed = "\u1100\u1175\u11B7\u1106\u1175\u11AB\u1109\u116E"; // 김민수 as jamo
        const string precomposed = "김민수";

        Assert.Equal(precomposed, NameNormalizer.Normalize(decomposed));
        Assert.Equal(NameNormalizer.Normalize(precomposed), NameNormalizer.Normalize(decomposed));
    }

    [Fact]
    public void Normalize_MapsCompatibilityJamoToConjoiningJamo()
    {
        // Verified against the real ICU-backed runtime: NFKC maps U+3131 (ㄱ, Hangul
        // Compatibility Jamo) to U+1100 (ᄀ, conjoining jamo choseong). The result is
        // retained because U+1100 falls in the first IsHangul range (U+1100..U+11FF).
        //
        // This is why the explicit IsHangul check matters: a lone conjoining jamo is
        // UnicodeCategory.OtherLetter and would pass IsLetterOrDigit anyway, but the
        // range keeps the intent documented. A standalone jamo is a degenerate name
        // that will not match any roster entry, which is the desired outcome.
        Assert.Equal("\u1100", NameNormalizer.Normalize("\u3131"));
    }

    [Fact]
    public void Normalize_StandaloneJamoDoesNotEqualPrecomposedSyllable()
    {
        // Guards against assuming jamo folding makes partial input match a real name.
        Assert.NotEqual(NameNormalizer.Normalize("김"), NameNormalizer.Normalize("\u3131"));
    }

    [Fact]
    public void Normalize_AppliesNfkcToFullWidthLatin()
    {
        // Full-width Latin (U+FF21..) is a Zoom/IME reality. NFKC folds it to ASCII,
        // then ToLowerInvariant lowercases it.
        Assert.Equal("jane", NameNormalizer.Normalize("\uFF2A\uFF41\uFF4E\uFF45"));
    }

    [Fact]
    public void Normalize_AppliesNfkcToFullWidthDigits()
    {
        Assert.Equal("123", NameNormalizer.Normalize("\uFF11\uFF12\uFF13"));
    }

    [Fact]
    public void Normalize_NfkcExpandsRomanNumeralLigature()
    {
        // U+2163 (Ⅳ) decomposes to "IV" under NFKC, then lowercases to "iv".
        Assert.Equal("iv", NameNormalizer.Normalize("\u2163"));
    }

    [Fact]
    public void Normalize_RemovesIdeographicSpace()
    {
        // U+3000 is SpaceSeparator; NFKC maps it to U+0020, which is then dropped.
        Assert.Equal("김민수", NameNormalizer.Normalize("김\u3000민수"));
    }

    [Fact]
    public void Normalize_RemovesNonBreakingSpace()
    {
        // U+00A0 is SpaceSeparator. Panel scrapes frequently carry these.
        Assert.Equal("janedoe", NameNormalizer.Normalize("Jane\u00A0Doe"));
    }

    [Fact]
    public void Normalize_RemovesZeroWidthSpace()
    {
        // U+200B is Format, not SpaceSeparator, so it survives to the
        // IsLetterOrDigit filter and is dropped there. Same net effect.
        Assert.Equal("janedoe", NameNormalizer.Normalize("Jane\u200BDoe"));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        // Required because normalized values are persisted and then re-normalized
        // on later reads (for example alias keys round-tripping through SQLite).
        const string input = "  김민수 (Seoul) \u3000 iPhone-2  ";
        var once = NameNormalizer.Normalize(input);
        var twice = NameNormalizer.Normalize(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Normalize_CollapsesCommonZoomDisplayNameVariantsToSameKey()
    {
        // The practical contract: these five spellings of one attendee must produce
        // one identity key, otherwise the panel watcher emits duplicate joins.
        var expected = NameNormalizer.Normalize("김민수");

        Assert.Equal(expected, NameNormalizer.Normalize(" 김민수"));
        Assert.Equal(expected, NameNormalizer.Normalize("김민수 "));
        Assert.Equal(expected, NameNormalizer.Normalize("김 민 수"));
        Assert.Equal(expected, NameNormalizer.Normalize("김민수\u00A0"));
        Assert.Equal(expected, NameNormalizer.Normalize("(김민수)"));
    }

    [Fact]
    public void Normalize_DoesNotConflateDistinctNames()
    {
        // Cheap guard against an over-aggressive future normalization.
        Assert.NotEqual(NameNormalizer.Normalize("김민수"), NameNormalizer.Normalize("김민서"));
        Assert.NotEqual(NameNormalizer.Normalize("Jane Doe"), NameNormalizer.Normalize("John Doe"));
    }
}
