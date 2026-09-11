using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ZoomCheck.Backend.Services;

/// <summary>
/// Minimal SemVer 2.0.0 parser/comparer good enough for release tags such as
/// "v0.6.42", "0.7.0-rc.1" and informational versions carrying a build suffix
/// like "0.6.42+abc1234" or "0.6.42-rc.1.5.gabc1234".
/// </summary>
public sealed record SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Null for stable releases.</summary>
    public string? Prerelease { get; }

    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static bool TryParse(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("V", StringComparison.Ordinal))
        {
            text = text[1..];
        }

        // Build metadata never participates in precedence, so drop it early.
        var plusIndex = text.IndexOf('+');
        if (plusIndex >= 0)
        {
            text = text[..plusIndex];
        }

        string? prerelease = null;
        var dashIndex = text.IndexOf('-');
        if (dashIndex >= 0)
        {
            prerelease = text[(dashIndex + 1)..];
            text = text[..dashIndex];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        // Assembly versions are frequently 4-part (major.minor.patch.revision);
        // the revision is folded away because SemVer has no slot for it.
        var numbers = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            numbers[i] = parsed;
        }

        if (parts.Length == 4
            && !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = Major.CompareTo(other.Major);
        if (core != 0)
        {
            return core;
        }

        core = Minor.CompareTo(other.Minor);
        if (core != 0)
        {
            return core;
        }

        core = Patch.CompareTo(other.Patch);
        if (core != 0)
        {
            return core;
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public override string ToString()
        => IsPrerelease
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{Prerelease}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static int ComparePrerelease(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right))
        {
            return 0;
        }

        // A stable release always outranks a prerelease of the same core version.
        if (string.IsNullOrEmpty(left))
        {
            return 1;
        }

        if (string.IsNullOrEmpty(right))
        {
            return -1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        var shared = Math.Min(leftParts.Length, rightParts.Length);
        for (var i = 0; i < shared; i++)
        {
            var comparison = ComparePrereleaseIdentifier(leftParts[i], rightParts[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftValue);
        var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightValue);

        return (leftNumeric, rightNumeric) switch
        {
            (true, true) => leftValue.CompareTo(rightValue),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right)
        };
    }
}
