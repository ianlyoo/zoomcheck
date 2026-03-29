using ZoomCheck.Core.Enums;

namespace ZoomCheck.Backend.Contracts;

public sealed record ParticipantEventRequest(
    ParticipantEventType EventType,
    string ParticipantName,
    string? ParticipantEmail,
    string Source,
    string RawPayload,
    DateTimeOffset? OccurredAt);
