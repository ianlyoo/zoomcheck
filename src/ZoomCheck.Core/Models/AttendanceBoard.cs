using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public sealed record AttendanceBoard(
    string MeetingId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<BoardPersonStatus> People,
    IReadOnlyList<UnmatchedParticipantStatus> UnmatchedParticipants,
    IReadOnlyList<ParticipantEvent> RecentEvents,
    IReadOnlyDictionary<MatchConfidence, int> ConfidenceCounts);
