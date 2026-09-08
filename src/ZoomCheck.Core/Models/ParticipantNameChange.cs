namespace ZoomCheck.Core.Models;

/// <summary>
/// A display-name change detected on a stable presence key that was already present.
/// Both the previous and the new name are preserved, raw and canonical.
/// </summary>
public sealed record ParticipantNameChange(
    string PresenceKey,
    string PreviousRawName,
    string PreviousName,
    string RawName,
    string Name,
    string? CanonicalName,
    DateTimeOffset OccurredAt);
