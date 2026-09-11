using System.Text.Json.Serialization;

namespace ZoomCheck.Backend.Contracts;

public sealed record MeetingExclusionRequest(bool? Excluded, string? AttendanceDate = null);
public sealed record MeetingPersonReviewRequest(string? Kind, string? Status, string? ExpectedEvidenceToken);
public sealed record MeetingConnectionMatchRequest(
    string? Source, string? PresenceKey, [property: JsonRequired] string? RosterPersonId, string? ExpectedEvidenceToken);
public sealed record MeetingConnectionReviewRequest(
    string? Source, string? PresenceKey, string? Status, string? ExpectedEvidenceToken);
