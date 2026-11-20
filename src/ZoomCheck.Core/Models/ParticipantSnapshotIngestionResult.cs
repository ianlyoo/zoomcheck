namespace ZoomCheck.Core.Models;

/// <summary>
/// Outcome of ingesting and persisting a participant snapshot without projecting a full board.
/// </summary>
public sealed record ParticipantSnapshotIngestionResult(
    string MeetingId,
    string Source,
    DateTimeOffset CapturedAt,
    int PresentCount,
    IReadOnlyList<string> JoinedNames,
    IReadOnlyList<string> LeftNames,
    IReadOnlyList<string> IgnoredNames,
    IReadOnlyList<ParticipantNameChange>? NameChanges = null);
