using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public sealed record ParticipantEvent(
    string Id,
    string MeetingId,
    DateTimeOffset OccurredAt,
    ParticipantEventType EventType,
    string ParticipantName,
    string NormalizedParticipantName,
    string? ParticipantEmail,
    MatchConfidence Confidence,
    string? MatchedRosterPersonId,
    string Source,
    string RawPayload);
