namespace ZoomCheck.Core.Models;

/// <summary>Operator decisions are scoped to a meeting and never change the imported roster.</summary>
public sealed record MeetingReviewDecision(
    string SubjectKey, string Kind, string Status, string EvidenceToken, DateTimeOffset UpdatedAt);

/// <summary>A single observed connection, not a global name alias.</summary>
public sealed record MeetingConnectionMatch(
    string Source, string PresenceKey, string RosterPersonId,
    string ObservedName, string ObservedEmail, DateTimeOffset FirstSeenAt, DateTimeOffset ObservedAt);
