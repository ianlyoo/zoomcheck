namespace ZoomCheck.Core.Models;

/// <summary>
/// Review metadata: several active presence keys currently resolve to the same roster person.
/// The person still counts as present exactly once; this group exists so an operator can decide
/// which connection is the real one.
/// </summary>
public sealed record DuplicateConnectionGroup(
    string RosterPersonId,
    string RosterPersonName,
    int ConnectionCount,
    IReadOnlyList<CurrentParticipantConnection> Connections);
