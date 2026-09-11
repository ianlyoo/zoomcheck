namespace ZoomCheck.Core.Models;

public sealed record RosterPerson(
    string Id,
    string Sequence,
    string Name,
    string NormalizedName,
    string Email,
    string Phone,
    string Organization,
    IReadOnlyList<string> Aliases,
    /// <summary>
    /// Optional roster grouping (조/분반/그룹/팀/반). Empty when the roster has no group column.
    /// Groups are presentation metadata only: they never take part in matching or in the
    /// stable person id, so re-importing a roster with regrouped people keeps attendance history.
    /// </summary>
    string Group = "");
