using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public sealed record ZoomParticipantEventInput(
    string MeetingId,
    DateTimeOffset OccurredAt,
    ParticipantEventType EventType,
    string ParticipantName,
    string? ParticipantEmail,
    string Source,
    string RawPayload);
