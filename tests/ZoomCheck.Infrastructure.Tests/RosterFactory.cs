using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Tests;

/// <summary>
/// Models legacy imports and changed sequence/name keys by generating fresh ids on each import.
/// Current Excel imports use stable keys; callers can also supply an id to exercise key reuse.
/// </summary>
internal static class RosterFactory
{
    public static RosterPerson Person(
        string name,
        string sequence = "1",
        string email = "",
        string phone = "",
        string organization = "",
        string? id = null)
    {
        var normalizedEmail = email.Trim().ToLowerInvariant();
        return new RosterPerson(
            Id: id ?? Guid.NewGuid().ToString("N"),
            Sequence: sequence,
            Name: name.Trim(),
            NormalizedName: NameNormalizer.Normalize(name),
            Email: normalizedEmail,
            Phone: phone,
            Organization: organization,
            Aliases: new[] { NameNormalizer.Normalize(name) });
    }

    public static RosterImportResult Import(params RosterPerson[] people)
        => new(
            ImportId: Guid.NewGuid().ToString("N"),
            SourcePath: Path.Combine(Path.GetTempPath(), "roster.xlsx"),
            DisplayName: "roster.xlsx",
            ImportedAt: DateTimeOffset.UtcNow,
            People: people);
}
