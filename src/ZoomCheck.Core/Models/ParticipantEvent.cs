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
    string RawPayload,
    string? PresenceKey = null,
    string? RawParticipantName = null,
    string? CanonicalParticipantName = null,
    string? PreviousParticipantName = null,
    string? PreviousRawParticipantName = null)
{
    /// <summary>Raw Zoom-reported name when available, otherwise the effective participant name.</summary>
    public string EffectiveRawParticipantName => string.IsNullOrWhiteSpace(RawParticipantName)
        ? ParticipantName
        : RawParticipantName;
}
