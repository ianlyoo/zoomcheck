using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using ZoomCheck.App.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.App.Services;

public sealed class ZoomParticipantPanelWatcher : IParticipantPanelWatcher
{
    private static readonly HashSet<string> ZoomProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Zoom",
        "CptHost"
    };

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

    private static readonly Regex ParticipantCountRegex = new(
        @"(?:participants?|참가자)\D{0,10}(?<count>\d{1,5})|(?<count>\d{1,5})\D{0,10}(?:participants?|참가자)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RoleSuffixRegex = new(
        @"\s*\((?=[^)]*(?:co-?host|host|me|공동\s*호스트|호스트|나))[^)]*\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly BackendApiClient _apiClient;
    private readonly TimeSpan _scanInterval = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;

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

        var capture = CaptureParticipantSnapshot();
        if (!capture.Success)
        {
            return Task.FromResult(UpdateStatus(false, false, capture.Names.Count, capture.Message, actionSucceeded: false));
        }

        return Task.FromResult(UpdateStatus(
            true,
            false,
            capture.Names.Count,
            $"Attached to Zoom. All {capture.ExpectedCount} participant row(s) were read and verified.",
            actionSucceeded: true));
    }

    public async Task<ParticipantPanelActionResult> ScanOnceAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            return UpdateStatus(
                CurrentStatus.IsAttached,
                CurrentStatus.IsMonitoring,
                CurrentStatus.VisibleParticipantCount,
                "Enter a meeting ID before scanning the participant panel.",
                actionSucceeded: false);
        }

        var capture = CaptureParticipantSnapshot();
        if (!capture.Success)
        {
            return UpdateStatus(
                CurrentStatus.IsAttached,
                CurrentStatus.IsMonitoring,
                capture.Names.Count,
                capture.Message,
                actionSucceeded: false);
        }

        var result = await _apiClient.PostParticipantSnapshotAsync(
            meetingId,
            capture.Names,
            source: BackendApiClient.OperatorParticipantSnapshotSource,
            cancellationToken);
        var changeCount = result.JoinedNames.Count + result.LeftNames.Count;
        var message = changeCount == 0
            ? $"Participant panel scanned. All {result.PresentCount} participant row(s) were verified; no changes detected."
            : $"Participant panel scanned. {result.PresentCount} present, {result.JoinedNames.Count} joined, and {result.LeftNames.Count} left.";

        return UpdateStatus(true, CurrentStatus.IsMonitoring, result.PresentCount, message, actionSucceeded: true);
    }

    public async Task<ParticipantPanelActionResult> StartMonitoringAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        await StopMonitoringAsync();
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            return UpdateStatus(
                CurrentStatus.IsAttached,
                false,
                CurrentStatus.VisibleParticipantCount,
                "Enter a meeting ID before starting participant monitoring.",
                actionSucceeded: false);
        }

        var attachResult = await AttachAsync(cancellationToken);
        if (!attachResult.Success)
        {
            return attachResult;
        }

        _monitoringCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loopToken = _monitoringCts.Token;

        _monitoringTask = Task.Run(async () =>
        {
            while (!loopToken.IsCancellationRequested)
            {
                try
                {
                    await ScanOnceAsync(meetingId, loopToken);
                }
                catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    UpdateStatus(
                        CurrentStatus.IsAttached,
                        true,
                        CurrentStatus.VisibleParticipantCount,
                        $"Participant monitoring error: {ex.Message}",
                        actionSucceeded: false);
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
        });

        return UpdateStatus(true, true, CurrentStatus.VisibleParticipantCount, "Monitoring started. ZoomCheck will submit only complete, count-verified participant snapshots.", actionSucceeded: true);
    }

    public async Task StopMonitoringAsync()
    {
        var monitoringCts = _monitoringCts;
        var monitoringTask = _monitoringTask;
        _monitoringCts = null;
        _monitoringTask = null;

        if (monitoringCts is null)
        {
            return;
        }

        monitoringCts.Cancel();
        if (monitoringTask is not null)
        {
            try
            {
                await monitoringTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        monitoringCts.Dispose();
        UpdateStatus(CurrentStatus.IsAttached, false, CurrentStatus.VisibleParticipantCount, "Monitoring stopped. You can scan manually or start monitoring again.", actionSucceeded: true);
    }

    public async ValueTask DisposeAsync()
    {
        await StopMonitoringAsync();
    }

    private ParticipantPanelCapture CaptureParticipantSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ParticipantPanelCapture.Failed("Zoom participant capture is only available on Windows.");
        }

        var currentProcessId = Environment.ProcessId;
        string? bestFailure = null;
        using var automation = new UIA3Automation();
        foreach (var process in Process.GetProcesses().Where(process => process.MainWindowHandle != IntPtr.Zero))
        {
            if (process.Id == currentProcessId || !ZoomProcessNames.Contains(process.ProcessName))
            {
                continue;
            }

            try
            {
                var window = automation.FromHandle(process.MainWindowHandle).AsWindow();
                var descendants = window.FindAllDescendants();
                var candidates = descendants
                    .Where(element => element.ControlType == ControlType.ListItem)
                    .Select(element => CleanParticipantName(element.Name))
                    .Where(ShouldKeepName)
                    .Cast<string>()
                    .GroupBy(NameNormalizer.Normalize, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
                var expectedCount = ParseExpectedParticipantCount(
                    descendants.Select(element => element.Name).Append(window.Name));

                if (expectedCount is null)
                {
                    bestFailure = "Zoom was found, but its total participant count could not be verified. Keep the Participants panel docked and visible, or use the manual paste fallback.";
                    continue;
                }

                if (candidates.Count != expectedCount.Value)
                {
                    bestFailure = $"Zoom reports {expectedCount.Value} participants, but ZoomCheck safely read {candidates.Count}. Expand the docked Participants panel until every row is visible, or use the manual paste fallback. No attendance changes were recorded.";
                    continue;
                }

                return ParticipantPanelCapture.Verified(candidates, expectedCount.Value);
            }
            catch (Exception ex)
            {
                bestFailure = $"Zoom was found, but its Participants panel could not be read: {ex.Message}";
            }
        }

        return ParticipantPanelCapture.Failed(
            bestFailure ?? "Could not find a readable Zoom Workplace window. Open and dock the Participants panel, keep the Zoom window visible, then try again.");
    }

    private ParticipantPanelActionResult UpdateStatus(
        bool isAttached,
        bool isMonitoring,
        int visibleParticipantCount,
        string message,
        bool? actionSucceeded = null)
    {
        CurrentStatus = new ParticipantPanelStatus(isAttached, isMonitoring, visibleParticipantCount, message, DateTimeOffset.Now);
        StatusChanged?.Invoke(this, CurrentStatus);
        return new ParticipantPanelActionResult(actionSucceeded ?? isAttached, visibleParticipantCount, message);
    }

    private static int? ParseExpectedParticipantCount(IEnumerable<string?> labels)
    {
        foreach (var label in labels.Where(label => !string.IsNullOrWhiteSpace(label)))
        {
            var match = ParticipantCountRegex.Match(label!);
            if (match.Success && int.TryParse(match.Groups["count"].Value, out var count))
            {
                return count;
            }
        }

        return null;
    }

    private static string? CleanParticipantName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var singleLine = name.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return RoleSuffixRegex.Replace(singleLine, string.Empty).Trim();
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

        return true;
    }

    private sealed record ParticipantPanelCapture(
        bool Success,
        IReadOnlyList<string> Names,
        int? ExpectedCount,
        string Message)
    {
        public static ParticipantPanelCapture Verified(IReadOnlyList<string> names, int expectedCount)
            => new(true, names, expectedCount, string.Empty);

        public static ParticipantPanelCapture Failed(string message)
            => new(false, Array.Empty<string>(), null, message);
    }
}
