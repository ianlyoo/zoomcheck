using System;
using System.Threading;
using System.Threading.Tasks;
using ZoomCheck.App.Models;

namespace ZoomCheck.App.Services;

public sealed class NoOpParticipantPanelWatcher : IParticipantPanelWatcher
{
    public event EventHandler<ParticipantPanelStatus>? StatusChanged;

    public ParticipantPanelStatus CurrentStatus { get; private set; } = new(false, false, 0, "Panel watcher is unavailable on this platform.", DateTimeOffset.UtcNow);

    public Task<ParticipantPanelActionResult> AttachAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Fail("Zoom participant capture is only available on Windows."));

    public Task<ParticipantPanelActionResult> ScanOnceAsync(string meetingId, CancellationToken cancellationToken = default)
        => Task.FromResult(Fail("Zoom participant capture is only available on Windows."));

    public Task<ParticipantPanelActionResult> StartMonitoringAsync(string meetingId, CancellationToken cancellationToken = default)
        => Task.FromResult(Fail("Zoom participant capture is only available on Windows."));

    public Task StopMonitoringAsync()
        => Task.CompletedTask;

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    private ParticipantPanelActionResult Fail(string message)
    {
        CurrentStatus = new ParticipantPanelStatus(false, false, 0, message, DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(this, CurrentStatus);
        return new ParticipantPanelActionResult(false, 0, message);
    }
}
