namespace ZoomCheck.Core.Models;

public sealed record RosterDuplicateNumberWarning(
    string Sequence,
    int Count,
    IReadOnlyList<string> Names,
    IReadOnlyList<int> RowNumbers);

public sealed record RosterImportResult(
    string ImportId,
    string SourcePath,
    string DisplayName,
    DateTimeOffset ImportedAt,
    IReadOnlyList<RosterPerson> People,
    IReadOnlyList<RosterDuplicateNumberWarning>? DuplicateNumbers = null);
