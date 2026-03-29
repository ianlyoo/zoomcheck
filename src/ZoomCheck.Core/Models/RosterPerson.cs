namespace ZoomCheck.Core.Models;

public sealed record RosterPerson(
    string Id,
    string Sequence,
    string Name,
    string NormalizedName,
    string Email,
    string Phone,
    string Organization,
    IReadOnlyList<string> Aliases);
