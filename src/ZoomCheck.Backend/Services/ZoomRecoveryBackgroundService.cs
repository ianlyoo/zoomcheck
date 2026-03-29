using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomRecoveryBackgroundService : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly ZoomRecoveryService _recoveryService;
    private readonly ZoomRecoveryOptions _options;
    private readonly Microsoft.Extensions.Logging.ILogger<ZoomRecoveryBackgroundService> _logger;

    public ZoomRecoveryBackgroundService(
        ZoomRecoveryService recoveryService,
        IOptions<ZoomRecoveryOptions> options,
        Microsoft.Extensions.Logging.ILogger<ZoomRecoveryBackgroundService> logger)
    {
        _recoveryService = recoveryService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Zoom late-start recovery is disabled.");
            return;
        }

        if (_options.StartupDelaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _recoveryService.RecoverLiveMeetingsAsync(stoppingToken);
                if (!result.Executed)
                {
                    _logger.LogWarning("Zoom late-start recovery skipped: {Warnings}", string.Join(" | ", result.Warnings));
                }
                else
                {
                    _logger.LogInformation(
                        "Zoom late-start recovery complete. users={UsersDiscovered} meetings={MeetingsDiscovered} insertedEvents={AddedParticipants}",
                        result.UsersDiscovered,
                        result.MeetingsDiscovered,
                        result.AddedParticipants);

                    if (result.Warnings.Count > 0)
                    {
                        _logger.LogWarning("Zoom late-start recovery warnings: {Warnings}", string.Join(" | ", result.Warnings));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Zoom late-start recovery failed.");
            }

            if (_options.PeriodicScanIntervalSeconds <= 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PeriodicScanIntervalSeconds), stoppingToken);
        }
    }
}
