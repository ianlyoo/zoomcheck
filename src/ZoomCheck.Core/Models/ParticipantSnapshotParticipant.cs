namespace ZoomCheck.Core.Models;

public sealed record ParticipantSnapshotParticipant(
    string PresenceKey,
    string DisplayName,
    string? Email = null);
