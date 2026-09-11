namespace ZoomCheck.Core.Models;

/// <summary>
/// One participant that a capture source reported as present at a point in time.
/// </summary>
public sealed record ParticipantSnapshotEntry(
    string NormalizedName,
    string DisplayName,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string? ParticipantEmail = null,
    string? PresenceKey = null,
    string? RawDisplayName = null,
    string? CanonicalName = null)
{
    /// <summary>The name exactly as reported by the capture source.</summary>
    public string EffectiveRawDisplayName => string.IsNullOrWhiteSpace(RawDisplayName)
        ? DisplayName
        : RawDisplayName;
}
