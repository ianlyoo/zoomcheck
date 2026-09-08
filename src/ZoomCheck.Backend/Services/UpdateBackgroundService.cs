using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

/// <summary>
/// Runs the startup update check, then optional periodic rechecks. Every failure
/// is swallowed here so the dashboard keeps running when GitHub is unreachable.
/// </summary>
public sealed class UpdateBackgroundService : BackgroundService
{
    private readonly UpdateService _updateService;
    private readonly UpdateOptions _options;
    private readonly ILogger<UpdateBackgroundService> _logger;

    public UpdateBackgroundService(
        UpdateService updateService,
        IOptions<UpdateOptions> options,
        ILogger<UpdateBackgroundService> logger)
    {
        _updateService = updateService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.CheckOnStartup)
        {
            return;
        }

        if (!_updateService.SupportedPlatform)
        {
            _logger.LogDebug("Skipping the update check: self-update is supported on Windows only.");
            return;
        }

        try
        {
            if (_options.StartupDelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var status = await _updateService.CheckAsync(manual: false, stoppingToken);
                    _logger.LogInformation(
                        "Update check finished. current={CurrentVersion} latest={LatestVersion} availability={Availability} download={DownloadState}",
                        status.CurrentVersion,
                        status.LatestVersion ?? "unknown",
                        status.Availability,
                        status.DownloadState);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Update check failed.");
                }

                if (_options.CheckIntervalHours <= 0)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromHours(_options.CheckIntervalHours), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }
}
