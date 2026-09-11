namespace ZoomCheck.Core.Models;

public sealed record ParticipantSnapshotSourceState(
    string MeetingId,
    string Source,
    DateTimeOffset CapturedAt,
    int PresentCount);
