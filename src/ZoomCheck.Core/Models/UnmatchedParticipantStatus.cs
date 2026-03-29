using ZoomCheck.Core.Enums;

namespace ZoomCheck.Core.Models;

public sealed record UnmatchedParticipantStatus(
    string ParticipantName,
    AttendanceState AttendanceState,
    DateTimeOffset LastSeenAt,
    int EventCount);
