using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomRelayBackgroundService : BackgroundService
{
    private readonly ZoomRelayService _relay;
    private readonly ZoomRelayOptions _options;
    private readonly ILogger<ZoomRelayBackgroundService> _logger;

    public ZoomRelayBackgroundService(
        ZoomRelayService relay,
        IOptions<ZoomRelayOptions> options,
        ILogger<ZoomRelayBackgroundService> logger)
    {
        _relay = relay;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_relay.IsConfigured)
        {
            return;
        }

        var delay = TimeSpan.FromMilliseconds(Math.Clamp(_options.PollIntervalMilliseconds, 500, 10_000));
        using var timer = new PeriodicTimer(delay);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _relay.PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ZoomAppBridgeException ex) when (ex.Error == ZoomAppBridgeError.UnconfirmedEmptySnapshot)
            {
                _logger.LogInformation("Relay received first empty participant snapshot; awaiting confirmation.");
            }
            catch (Exception ex)
            {
                // Never log encrypted payloads, session identifiers, bearer tokens, or pairing codes.
                _logger.LogWarning("Zoom relay polling failed ({ErrorType}).", ex.GetType().Name);
            }
        }
    }
}
