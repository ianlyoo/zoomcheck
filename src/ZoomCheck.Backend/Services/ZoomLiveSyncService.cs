using ZoomCheck.Backend.Contracts;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomLiveSyncService
{
    public const string SnapshotSource = "zoom-live-participants";

    private readonly ZoomApiClient _zoomApiClient;
    private readonly AttendanceApplicationService _attendanceService;

    public ZoomLiveSyncService(ZoomApiClient zoomApiClient, AttendanceApplicationService attendanceService)
    {
        _zoomApiClient = zoomApiClient;
        _attendanceService = attendanceService;
    }

    public async Task<ZoomLiveSyncResponse> SyncAsync(
        string meetingId,
        bool allowEmptySnapshot = false,
        CancellationToken cancellationToken = default)
    {
        var normalizedMeetingId = MeetingIdNormalizer.Normalize(meetingId);
        var apiParticipants = await _zoomApiClient.GetLiveParticipantsAsync(normalizedMeetingId, cancellationToken);

        var active = apiParticipants
            .Where(IsActiveParticipant)
            .Where(participant => !string.IsNullOrWhiteSpace(participant.EffectiveName))
            .ToArray();

        if (active.Length == 0 && !allowEmptySnapshot)
        {
            throw new ZoomLiveSyncException(
                "Zoom returned no active participants. The existing attendance snapshot was left unchanged.");
        }

        var names = active.Select(participant => participant.EffectiveName!.Trim()).ToArray();
        var emails = active
            .GroupBy(participant => participant.EffectiveName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.EffectiveEmail).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
        var participants = active.Select(participant => new ParticipantSnapshotParticipant(
            BuildPresenceKey(participant),
            participant.EffectiveName!.Trim(),
            participant.EffectiveEmail)).ToArray();
        var capturedAt = DateTimeOffset.UtcNow;

        var snapshot = await _attendanceService.ApplyParticipantSnapshotAsync(
            new ParticipantSnapshotInput(
                normalizedMeetingId,
                names,
                SnapshotSource,
                capturedAt,
                emails,
                participants),
            cancellationToken);

        return new ZoomLiveSyncResponse(
            normalizedMeetingId,
            capturedAt,
            apiParticipants.Count,
            snapshot.PresentCount,
            apiParticipants.Count - active.Length,
            snapshot,
            snapshot.Board.CurrentConnections,
            snapshot.Board.DuplicateConnectionGroups,
            snapshot.NameChanges);
    }

    private static bool IsActiveParticipant(ZoomMeetingParticipant participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.Status))
        {
            return string.Equals(participant.Status, "in_meeting", StringComparison.OrdinalIgnoreCase);
        }

        return string.IsNullOrWhiteSpace(participant.LeaveTime);
    }

    private static string BuildPresenceKey(ZoomMeetingParticipant participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.Id))
        {
            return $"zoom-id:{participant.Id.Trim()}";
        }

        if (!string.IsNullOrWhiteSpace(participant.UserId))
        {
            return $"zoom-user:{participant.UserId.Trim()}";
        }

        var normalizedName = ZoomCheck.Core.Services.NameNormalizer.Normalize(participant.EffectiveName);
        var normalizedEmail = participant.EffectiveEmail?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedEmail)
            ? $"zoom-name:{normalizedName}"
            : $"zoom-name:{normalizedName}|{normalizedEmail}";
    }

}

public sealed class ZoomLiveSyncException : Exception
{
    public ZoomLiveSyncException(string message) : base(message)
    {
    }
}
