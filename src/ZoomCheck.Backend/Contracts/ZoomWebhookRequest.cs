namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomWebhookRequest(
    string MeetingId,
    string EventType,
    string ParticipantName,
    string? ParticipantEmail,
    DateTimeOffset? OccurredAt,
    string Source);
