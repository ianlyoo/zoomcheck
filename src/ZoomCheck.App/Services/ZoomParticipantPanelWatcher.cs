using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomCheck.App.Models;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Services;

namespace ZoomCheck.App.Services;

public sealed class ZoomParticipantPanelWatcher : IParticipantPanelWatcher
{
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Participants",
        "Invite",
        "Mute All",
        "Unmute All",
        "More",
        "Search",
        "Chat",
        "Host",
        "Co-Host"
    };

    private readonly BackendApiClient _apiClient;
    private readonly object _sync = new();
    private readonly Dictionary<string, string> _activeParticipants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _missingCounts = new(StringComparer.Ordinal);
    private readonly TimeSpan _scanInterval = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _monitoringCts;

    public ZoomParticipantPanelWatcher(BackendApiClient apiClient)
    {
        _apiClient = apiClient;
        CurrentStatus = new ParticipantPanelStatus(false, false, 0, "Open the Zoom participant panel, then attach ZoomCheck.", DateTimeOffset.UtcNow);
    }

    public event EventHandler<ParticipantPanelStatus>? StatusChanged;

    public ParticipantPanelStatus CurrentStatus { get; private set; }

    public Task<ParticipantPanelActionResult> AttachAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(UpdateStatus(false, false, 0, "Zoom participant capture is only available on Windows."));
        }

        var participants = ExtractVisibleParticipants();
        if (participants.Count == 0)
        {
            return Task.FromResult(UpdateStatus(false, false, 0, "Could not read the Zoom participant panel. Open Participants in Zoom and keep the Zoom window visible, then try again."));
        }

        lock (_sync)
        {
            _activeParticipants.Clear();
            _missingCounts.Clear();
        }

        return Task.FromResult(UpdateStatus(true, false, participants.Count, $"Attached to Zoom. {participants.Count} visible participant row(s) detected."));
    }

    public async Task<ParticipantPanelActionResult> ScanOnceAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            return UpdateStatus(CurrentStatus.IsAttached, CurrentStatus.IsMonitoring, CurrentStatus.VisibleParticipantCount, "Enter a meeting ID before scanning the participant panel.");
        }

        var participants = ExtractVisibleParticipants();
        if (participants.Count == 0)
        {
            return UpdateStatus(false, CurrentStatus.IsMonitoring, 0, "No participant names could be read. Keep the Zoom participants panel open and visible.");
        }

        var events = BuildParticipantEvents(participants);
        if (events.Count > 0)
        {
            await _apiClient.PostParticipantEventsAsync(meetingId, events, cancellationToken);
        }

        var message = events.Count == 0
            ? $"Participant panel scanned. {participants.Count} visible row(s) read; no join/leave changes detected."
            : $"Participant panel scanned. {participants.Count} visible row(s) read and {events.Count} attendance change(s) recorded.";

        return UpdateStatus(true, CurrentStatus.IsMonitoring, participants.Count, message);
    }

    public async Task<ParticipantPanelActionResult> StartMonitoringAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var attachResult = await AttachAsync(cancellationToken);
        if (!attachResult.Success)
        {
            return attachResult;
        }

        await StopMonitoringAsync();
        _monitoringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loopToken = _monitoringCts.Token;

        _ = Task.Run(async () =>
        {
            while (!loopToken.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(meetingId, loopToken);
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(_scanInterval, loopToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, loopToken);

        return UpdateStatus(true, true, CurrentStatus.VisibleParticipantCount, "Monitoring started. ZoomCheck will keep reading the participant panel and recording join/leave changes.");
    }

    public Task StopMonitoringAsync()
    {
        if (_monitoringCts is not null)
        {
            _monitoringCts.Cancel();
            _monitoringCts.Dispose();
            _monitoringCts = null;
        }

        UpdateStatus(CurrentStatus.IsAttached, false, CurrentStatus.VisibleParticipantCount, "Monitoring stopped. You can scan manually or start monitoring again.");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopMonitoringAsync();
    }

    private List<ObservedParticipantEventRequest> BuildParticipantEvents(IReadOnlyList<string> participants)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = participants
            .Select(name => new { Raw = name, Normalized = NameNormalizer.Normalize(name) })
            .Where(item => !string.IsNullOrWhiteSpace(item.Normalized))
            .GroupBy(item => item.Normalized)
            .Select(group => group.First())
            .ToList();

        var currentKeys = snapshot.Select(item => item.Normalized).ToHashSet(StringComparer.Ordinal);
        var events = new List<ObservedParticipantEventRequest>();

        lock (_sync)
        {
            foreach (var item in snapshot)
            {
                if (_activeParticipants.TryAdd(item.Normalized, item.Raw))
                {
                    events.Add(new ObservedParticipantEventRequest(
                        ParticipantEventType.Joined,
                        item.Raw,
                        null,
                        "panel-uia",
                        JsonSerializer.Serialize(new { source = "panel-uia", mode = "join", visibleParticipants = participants.Count }),
                        now));
                }

                _activeParticipants[item.Normalized] = item.Raw;
                _missingCounts.Remove(item.Normalized);
            }

            foreach (var existing in _activeParticipants.Keys.ToList())
            {
                if (currentKeys.Contains(existing))
                {
                    continue;
                }

                var missingCount = _missingCounts.TryGetValue(existing, out var current) ? current + 1 : 1;
                _missingCounts[existing] = missingCount;

                if (missingCount < 2)
                {
                    continue;
                }

                var displayName = _activeParticipants[existing];
                events.Add(new ObservedParticipantEventRequest(
                    ParticipantEventType.Left,
                    displayName,
                    null,
                    "panel-uia",
                    JsonSerializer.Serialize(new { source = "panel-uia", mode = "left", visibleParticipants = participants.Count }),
                    now));

                _activeParticipants.Remove(existing);
                _missingCounts.Remove(existing);
            }
        }

        return events;
    }

    private List<string> ExtractVisibleParticipants()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new List<string>();
        }

        using var automation = new UIA3Automation();
        foreach (var process in Process.GetProcesses().Where(process => process.MainWindowHandle != IntPtr.Zero))
        {
            if (!process.ProcessName.Contains("zoom", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var window = automation.FromHandle(process.MainWindowHandle).AsWindow();
                var candidates = window.FindAllDescendants()
                    .Where(element => element.ControlType is ControlType.ListItem or ControlType.Text)
                    .Select(element => element.Name?.Trim())
                    .Where(ShouldKeepName)
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (candidates.Count > 0)
                {
                    return candidates;
                }
            }
            catch
            {
            }
        }

        return new List<string>();
    }

    private ParticipantPanelActionResult UpdateStatus(bool isAttached, bool isMonitoring, int visibleParticipantCount, string message)
    {
        CurrentStatus = new ParticipantPanelStatus(isAttached, isMonitoring, visibleParticipantCount, message, DateTimeOffset.Now);
        StatusChanged?.Invoke(this, CurrentStatus);
        return new ParticipantPanelActionResult(isAttached || CurrentStatus.IsAttached, visibleParticipantCount, message);
    }

    private static bool ShouldKeepName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 80)
        {
            return false;
        }

        if (IgnoredNames.Contains(trimmed))
        {
            return false;
        }

        if (trimmed.Contains("participant", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("invite", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("mute", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("chat", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("reaction", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
