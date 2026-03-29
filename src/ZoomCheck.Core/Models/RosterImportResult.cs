namespace ZoomCheck.Core.Models;

public sealed record RosterImportResult(
    string ImportId,
    string SourcePath,
    string DisplayName,
    DateTimeOffset ImportedAt,
    IReadOnlyList<RosterPerson> People);
