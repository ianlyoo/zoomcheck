using System.Diagnostics;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class DashboardBrowserLauncher : BackgroundService
{
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly DashboardOptions _options;
    private readonly ILogger<DashboardBrowserLauncher> _logger;

    public DashboardBrowserLauncher(
        IHostApplicationLifetime applicationLifetime,
        IOptions<DashboardOptions> options,
        ILogger<DashboardBrowserLauncher> logger)
    {
        _applicationLifetime = applicationLifetime;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || !_options.OpenBrowserOnStart)
        {
            return;
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = _applicationLifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);
        await Task.Delay(500, stoppingToken);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _options.Url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the ZoomCheck dashboard at {DashboardUrl}", _options.Url);
        }
    }
}
