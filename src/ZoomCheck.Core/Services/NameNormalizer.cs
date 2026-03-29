using System.Globalization;
using System.Text;

namespace ZoomCheck.Core.Services;

public static class NameNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch) || IsHangul(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsHangul(char ch) => ch is >= '\u1100' and <= '\u11FF'
        or >= '\u3130' and <= '\u318F'
        or >= '\uAC00' and <= '\uD7AF';
}
