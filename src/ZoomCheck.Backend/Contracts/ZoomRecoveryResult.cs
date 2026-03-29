namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomRecoveryResult(
    bool Executed,
    int UsersDiscovered,
    int MeetingsDiscovered,
    int AddedParticipants,
    IReadOnlyList<RecoveredMeetingResult> Meetings,
    IReadOnlyList<string> Warnings,
    string? Error);

public sealed record RecoveredMeetingResult(
    string MeetingId,
    int DiscoveredParticipants,
    int AddedEvents);


public sealed record ZoomRecoveryLastRun(
    DateTimeOffset? LastRunAt,
    ZoomRecoveryResult? LastResult);
