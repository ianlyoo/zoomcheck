namespace ZoomCheck.Core.Models;

/// <summary>
/// One participant that a capture source reported as present at a point in time.
/// </summary>
public sealed record ParticipantSnapshotEntry(
    string NormalizedName,
    string DisplayName,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);
