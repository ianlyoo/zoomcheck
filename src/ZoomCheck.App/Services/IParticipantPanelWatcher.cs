using System;
using System.Threading;
using System.Threading.Tasks;
using ZoomCheck.App.Models;

namespace ZoomCheck.App.Services;

public interface IParticipantPanelWatcher : IAsyncDisposable
{
    event EventHandler<ParticipantPanelStatus>? StatusChanged;

    ParticipantPanelStatus CurrentStatus { get; }

    Task<ParticipantPanelActionResult> AttachAsync(CancellationToken cancellationToken = default);

    Task<ParticipantPanelActionResult> ScanOnceAsync(string meetingId, CancellationToken cancellationToken = default);

    Task<ParticipantPanelActionResult> StartMonitoringAsync(string meetingId, CancellationToken cancellationToken = default);

    Task StopMonitoringAsync();
}
