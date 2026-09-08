using ZoomCheck.Core.Services;

namespace ZoomCheck.Tests;

public sealed class MeetingIdNormalizerTests
{
    [Theory]
    [InlineData("123 456 78901", "12345678901")]
    [InlineData("123-456-78901", "12345678901")]
    [InlineData(" 12345678901 ", "12345678901")]
    [InlineData("meeting-uuid/value", "meeting-uuid/value")]
    [InlineData("abc 123", "abc 123")]
    public void Normalize_CanonicalizesNumericZoomIdsAndPreservesOpaqueIds(string input, string expected)
    {
        Assert.Equal(expected, MeetingIdNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" - - " )]
    public void TryNormalize_RejectsEmptyIds(string? input)
    {
        Assert.False(MeetingIdNormalizer.TryNormalize(input, out _));
    }
}
