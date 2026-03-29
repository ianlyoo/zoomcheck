using System.Text.Json;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomRecoveryService
{
    private readonly ZoomApiClient _zoomApiClient;
    private readonly AttendanceApplicationService _attendanceService;
    private readonly ZoomRecoveryOptions _recoveryOptions;

    private readonly object _stateLock = new();
    private DateTimeOffset? _lastRunAt;
    private ZoomRecoveryResult? _lastResult;

    public ZoomRecoveryService(
        ZoomApiClient zoomApiClient,
        AttendanceApplicationService attendanceService,
        IOptions<ZoomRecoveryOptions> recoveryOptions)
    {
        _zoomApiClient = zoomApiClient;
        _attendanceService = attendanceService;
        _recoveryOptions = recoveryOptions.Value;
    }

    public ZoomRecoveryLastRun GetLastRun()
    {
        lock (_stateLock)
        {
            return new ZoomRecoveryLastRun(_lastRunAt, _lastResult);
        }
    }

    public async Task<ZoomRecoveryResult> RecoverLiveMeetingsAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        try
        {
            var userIds = await ResolveUserIdsAsync(cancellationToken);
            if (userIds.Count == 0)
            {
                return RecordResult(new ZoomRecoveryResult(
                    Executed: false,
                    UsersDiscovered: 0,
                    MeetingsDiscovered: 0,
                    AddedParticipants: 0,
                    Meetings: Array.Empty<RecoveredMeetingResult>(),
                    Warnings: new[]
                    {
                        "No recovery user targets were configured. Add ZoomRecovery:HostUserIds, or enable ZoomRecovery:EnableAccountWideUserDiscovery."
                    },
                    Error: null));
            }

            var meetingsToRecover = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var userId in userIds)
            {
                try
                {
                    var meetings = await _zoomApiClient.GetLiveMeetingsForUserAsync(userId, cancellationToken);
                    foreach (var meeting in meetings)
                    {
                        if (string.IsNullOrWhiteSpace(meeting.Id))
                        {
                            continue;
                        }

                        meetingsToRecover[meeting.Id] = meeting.Topic ?? string.Empty;
                    }
                }
                catch (ZoomApiException ex)
                {
                    warnings.Add($"Could not list live meetings for user '{userId}'. Zoom returned {(int)ex.StatusCode}. {ex.Message}");
                }
            }

            if (meetingsToRecover.Count == 0)
            {
                return RecordResult(new ZoomRecoveryResult(
                    Executed: true,
                    UsersDiscovered: userIds.Count,
                    MeetingsDiscovered: 0,
                    AddedParticipants: 0,
                    Meetings: Array.Empty<RecoveredMeetingResult>(),
                    Warnings: warnings,
                    Error: null));
            }

            var meetingResults = new List<RecoveredMeetingResult>();
            var addedParticipants = 0;

            foreach (var kv in meetingsToRecover)
            {
                var meetingId = kv.Key;
                var meetingTopic = kv.Value;

                try
                {
                    var inserted = await RecoverMeetingParticipantsAsync(meetingId, meetingTopic, cancellationToken);
                    meetingResults.Add(new RecoveredMeetingResult(meetingId, inserted.discoveredParticipants, inserted.newEvents));
                    addedParticipants += inserted.newEvents;
                }
                catch (ZoomApiException ex)
                {
                    warnings.Add($"Could not fetch participants for meeting '{meetingId}'. Zoom returned {(int)ex.StatusCode}. {ex.Message}");
                }
            }

            return RecordResult(new ZoomRecoveryResult(
                Executed: true,
                UsersDiscovered: userIds.Count,
                MeetingsDiscovered: meetingsToRecover.Count,
                AddedParticipants: addedParticipants,
                Meetings: meetingResults,
                Warnings: warnings,
                Error: null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RecordResult(new ZoomRecoveryResult(
                Executed: false,
                UsersDiscovered: 0,
                MeetingsDiscovered: 0,
                AddedParticipants: 0,
                Meetings: Array.Empty<RecoveredMeetingResult>(),
                Warnings: warnings,
                Error: ex.Message));
        }
    }

    private async Task<List<string>> ResolveUserIdsAsync(CancellationToken cancellationToken)
    {
        var userIds = new HashSet<string>(StringComparer.Ordinal);

        if (_recoveryOptions.HostUserIds is { Length: > 0 })
        {
            foreach (var userId in _recoveryOptions.HostUserIds)
            {
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    userIds.Add(userId.Trim());
                }
            }
        }

        if (_recoveryOptions.EnableAccountWideUserDiscovery)
        {
            var users = await _zoomApiClient.GetActiveUsersAsync(cancellationToken);
            foreach (var user in users)
            {
                if (!string.IsNullOrWhiteSpace(user.Id))
                {
                    userIds.Add(user.Id);
                }
            }
        }

        if (userIds.Count == 0 && _recoveryOptions.IncludeFallbackMeUser)
        {
            userIds.Add("me");
        }

        return userIds.ToList();
    }

    private async Task<(int discoveredParticipants, int newEvents)> RecoverMeetingParticipantsAsync(string meetingId, string meetingTopic, CancellationToken cancellationToken)
    {
        var existingEvents = await _attendanceService.GetParticipantEventsForMeetingAsync(meetingId, cancellationToken);
        var activeKeys = GetCurrentlyActiveParticipantKeys(existingEvents);
        var liveParticipants = await _zoomApiClient.GetLiveParticipantsAsync(meetingId, cancellationToken);

        var newEvents = 0;
        foreach (var participant in liveParticipants)
        {
            var name = participant.UserName ?? participant.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = BuildIdentityKey(name, participant.UserEmail);
            if (activeKeys.Contains(key))
            {
                continue;
            }

            var occurredAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrWhiteSpace(participant.JoinTime) && DateTimeOffset.TryParse(participant.JoinTime, out var parsedJoinTime))
            {
                occurredAt = parsedJoinTime;
            }

            await _attendanceService.RecordZoomEventAsync(new ZoomParticipantEventInput(
                meetingId,
                occurredAt,
                Core.Enums.ParticipantEventType.Joined,
                name,
                participant.UserEmail,
                "zoom-live-recovery",
                JsonSerializer.Serialize(new
                {
                    source = "zoom-live-recovery",
                    meetingId,
                    meetingTopic,
                    participantId = participant.Id,
                    userId = participant.UserId
                })),
                cancellationToken);

            activeKeys.Add(key);
            newEvents++;
        }

        return (discoveredParticipants: liveParticipants.Count, newEvents: newEvents);
    }

    private ZoomRecoveryResult RecordResult(ZoomRecoveryResult result)
    {
        lock (_stateLock)
        {
            _lastResult = result;
            _lastRunAt = DateTimeOffset.UtcNow;
        }

        return result;
    }

    private static HashSet<string> GetCurrentlyActiveParticipantKeys(IReadOnlyList<ParticipantEvent> events)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);

        var activeByIdentity = events
            .GroupBy(evt => BuildIdentityKey(evt.ParticipantName, evt.ParticipantEmail))
            .Select(group => group.OrderBy(evt => evt.OccurredAt).Last())
            .Where(evt => evt.EventType == Core.Enums.ParticipantEventType.Joined)
            .Select(evt => BuildIdentityKey(evt.ParticipantName, evt.ParticipantEmail));

        foreach (var key in activeByIdentity)
        {
            active.Add(key);
        }

        return active;
    }

    private static string BuildIdentityKey(string? name, string? email)
    {
        var normalizedName = NameNormalizer.Normalize(name);
        var normalizedEmail = email?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedEmail)
            ? normalizedName
            : $"{normalizedName}|{normalizedEmail}";
    }
}
