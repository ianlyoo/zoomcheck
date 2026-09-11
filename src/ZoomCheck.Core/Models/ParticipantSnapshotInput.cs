namespace ZoomCheck.Core.Models;

/// <summary>
/// A full "who is present right now" capture for a meeting, produced by a single capture source
/// (for example Windows UI Automation or an operator-pasted fallback snapshot).
/// </summary>
public sealed record ParticipantSnapshotInput(
    string MeetingId,
    IReadOnlyList<string> ParticipantNames,
    string Source,
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, string?>? ParticipantEmails = null,
    IReadOnlyList<ParticipantSnapshotParticipant>? Participants = null);
