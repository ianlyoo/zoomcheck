namespace ZoomCheck.Core.Services;

public static class MeetingIdNormalizer
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > 512)
        {
            normalized = string.Empty;
            return false;
        }

        if (normalized.All(character =>
                char.IsDigit(character) || char.IsWhiteSpace(character) || character == '-'))
        {
            normalized = new string(normalized.Where(char.IsDigit).ToArray());
        }

        return normalized.Length is > 0 and <= 512;
    }

    public static string Normalize(string? value, string parameterName = "meetingId")
        => TryNormalize(value, out var normalized)
            ? normalized
            : throw new ArgumentException("Meeting id is required and must be valid.", parameterName);
}
