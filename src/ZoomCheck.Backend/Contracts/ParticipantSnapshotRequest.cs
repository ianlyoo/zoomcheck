namespace ZoomCheck.Backend.Contracts;

/// <summary>
/// Full "currently present" participant list captured from a single source
/// (for example the macOS manual snapshot of the Zoom participant panel).
/// </summary>
public sealed record ParticipantSnapshotRequest(
    IReadOnlyList<string>? ParticipantNames,
    string? Source,
    DateTimeOffset? CapturedAt);
