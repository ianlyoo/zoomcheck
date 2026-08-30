namespace ZoomCheck.Backend.Contracts;

/// <summary>
/// Full "currently present" participant list captured from a single source
/// (for example the macOS manual snapshot of the Zoom participant panel).
/// </summary>
/// <remarks>
/// <paramref name="ParticipantNames"/> stays required for backwards compatibility with existing
/// manual snapshot clients. <paramref name="Participants"/> is optional and lets a capture source
/// supply stable presence keys and emails so renames can be detected per connection.
/// </remarks>
public sealed record ParticipantSnapshotRequest(
    IReadOnlyList<string>? ParticipantNames,
    string? Source,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<ParticipantSnapshotConnectionRequest>? Participants = null);

/// <summary>One connection in a participant snapshot, keyed by a stable presence key.</summary>
public sealed record ParticipantSnapshotConnectionRequest(
    string? PresenceKey,
    string? DisplayName,
    string? Email);
