using System.Text;
using ZoomCheck.Core.Models;

namespace ZoomCheck.Infrastructure.Services;

internal static class AttendanceBoardCsvWriter
{
    public static string Write(AttendanceBoard board, string? groupFilter)
    {
        var normalizedFilter = NormalizeGroup(groupFilter);
        var rows = board.People
            .Where(person => !person.IsExcluded)
            .Where(person => string.IsNullOrEmpty(normalizedFilter) || string.Equals(
                    NormalizeGroup(person.Group),
                    normalizedFilter,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var builder = new StringBuilder();
        builder.AppendLine("Sequence,Name,Organization,Group,AttendanceState,Confidence,ConfidenceReason,LastJoinedAt,LastLeftAt,JoinCount,IdentityReviewStatus,DuplicateReviewStatus");

        foreach (var person in rows)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                Escape(person.Sequence),
                Escape(person.Name),
                Escape(person.Organization),
                Escape(person.Group),
                Escape(person.AttendanceState.ToString()),
                Escape(person.Confidence.ToString()),
                Escape(person.ConfidenceReason),
                Escape(person.LastJoinedAt?.ToString("O") ?? string.Empty),
                Escape(person.LastLeftAt?.ToString("O") ?? string.Empty),
                Escape(person.JoinCount.ToString()),
                Escape(person.IdentityReviewStatus),
                Escape(person.DuplicateReviewStatus)
            }));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Trims and collapses whitespace runs so group values written with inconsistent spacing
    /// still compare equal. Mirrors the normalization the roster parser applies on import.
    /// </summary>
    private static string NormalizeGroup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
