namespace ZoomCheck.Core.Models;

/// <summary>
/// Outcome of applying a full participant snapshot: which names newly joined, which ones left,
/// and the refreshed board.
/// </summary>
public sealed record ParticipantSnapshotResult(
    string MeetingId,
    string Source,
    DateTimeOffset CapturedAt,
    int PresentCount,
    IReadOnlyList<string> JoinedNames,
    IReadOnlyList<string> LeftNames,
    IReadOnlyList<string> IgnoredNames,
    AttendanceBoard Board);
