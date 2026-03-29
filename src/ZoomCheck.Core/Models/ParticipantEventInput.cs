using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public record ParticipantEventInput(
    string MeetingId,
    DateTimeOffset OccurredAt,
    ParticipantEventType EventType,
    string ParticipantName,
    string? ParticipantEmail,
    string Source,
    string RawPayload);
