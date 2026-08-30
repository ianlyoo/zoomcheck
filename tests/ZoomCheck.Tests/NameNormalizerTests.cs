using ZoomCheck.Core.Services;

namespace ZoomCheck.Tests;

public class NameNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Normalize_ReturnsEmpty_ForBlankInput(string? value)
    {
        Assert.Equal(string.Empty, NameNormalizer.Normalize(value));
    }

    [Theory]
    [InlineData("김영인", "김영인")]
    [InlineData("김 영 인", "김영인")]
    [InlineData("  김영인  ", "김영인")]
    [InlineData("김영인\u00A0", "김영인")]
    [InlineData("김영인\u3000교사", "김영인교사")]
    public void Normalize_RemovesAllWhitespaceVariants_ForKoreanNames(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("김영인(교사)", "김영인교사")]
    [InlineData("김영인 / 서울대", "김영인서울대")]
    [InlineData("김영인-2", "김영인2")]
    [InlineData("[김영인]", "김영인")]
    [InlineData("김영인.", "김영인")]
    [InlineData("김·영·인", "김영인")]
    public void Normalize_DropsPunctuation(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_LowercasesLatinAndKeepsDigits()
    {
        Assert.Equal("kimyoungin7", NameNormalizer.Normalize("Kim YoungIn 7"));
    }

    [Fact]
    public void Normalize_AppliesFormKcCompatibilityFolding()
    {
        // Full-width Latin letters fold to their ASCII equivalents under FormKC.
        Assert.Equal("kim", NameNormalizer.Normalize("\uFF2B\uFF29\uFF2D"));
    }

    [Fact]
    public void Normalize_ComposesDecomposedHangul()
    {
        // Conjoining jamo (U+1100 U+1161) compose to the precomposed syllable 가 (U+AC00).
        var decomposed = "\u1100\u1161";
        Assert.Equal("\uAC00", NameNormalizer.Normalize(decomposed));
        Assert.Equal(NameNormalizer.Normalize("가"), NameNormalizer.Normalize(decomposed));
    }

    [Fact]
    public void Normalize_MapsCompatibilityJamoToConjoiningJamo()
    {
        // FormKC maps the compatibility jamo ㄱ (U+3131) to the conjoining jamo U+1100,
        // which the Hangul range check still preserves.
        Assert.Equal("\u1100", NameNormalizer.Normalize("\u3131"));
    }

    [Fact]
    public void Normalize_IsStableWhenAppliedTwice()
    {
        var once = NameNormalizer.Normalize(" 김 영인 (Kim) ");
        Assert.Equal(once, NameNormalizer.Normalize(once));
    }

    [Fact]
    public void Normalize_TreatsDifferentSpacingOfSameNameAsEqual()
    {
        Assert.Equal(NameNormalizer.Normalize("이 순신"), NameNormalizer.Normalize("이순신"));
    }

    [Fact]
    public void Normalize_ReturnsEmpty_WhenInputIsOnlyPunctuation()
    {
        Assert.Equal(string.Empty, NameNormalizer.Normalize("--- ()"));
    }
}
