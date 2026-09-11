namespace ZoomCheck.Relay.Options;

/// <summary>
/// Relay tuning knobs. Every value has a conservative default so the relay is safe with no
/// configuration at all; configuration can only be used to adjust, never to disable, limits.
/// </summary>
public sealed class RelayOptions
{
    public const string SectionName = "Relay";

    /// <summary>Lifetime of a pairing code before it can no longer be redeemed.</summary>
    public TimeSpan PairingCodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Lifetime of a paired session, refreshed on each successful authenticated call.</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Hard cap on concurrent sessions held in memory.</summary>
    public int MaxSessions { get; set; } = 500;

    /// <summary>Maximum undelivered envelopes per direction before the queue is considered full.</summary>
    public int MaxQueuedMessagesPerDirection { get; set; } = 200;

    /// <summary>Maximum accepted request body size in bytes.</summary>
    public long MaxRequestBodyBytes { get; set; } = 768 * 1024;

    /// <summary>Requests allowed per client key inside <see cref="RateLimitWindow"/>.</summary>
    public int RateLimitRequestsPerWindow { get; set; } = 1_000;

    /// <summary>Sliding-ish fixed window used for rate limiting.</summary>
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Failed pairing redemptions allowed per client key inside the rate limit window.</summary>
    public int MaxPairingFailuresPerWindow { get; set; } = 10;

    /// <summary>
    /// Applies floors and ceilings so a misconfiguration cannot widen the relay's attack surface.
    /// </summary>
    public RelayOptions Normalized()
    {
        return new RelayOptions
        {
            PairingCodeLifetime = Clamp(PairingCodeLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)),
            SessionIdleTimeout = Clamp(SessionIdleTimeout, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12)),
            MaxSessions = Math.Clamp(MaxSessions, 1, 10_000),
            MaxQueuedMessagesPerDirection = Math.Clamp(MaxQueuedMessagesPerDirection, 1, 2_000),
            MaxRequestBodyBytes = Math.Clamp(MaxRequestBodyBytes, 1_024, 1_048_576),
            RateLimitRequestsPerWindow = Math.Clamp(RateLimitRequestsPerWindow, 1, 100_000),
            RateLimitWindow = Clamp(RateLimitWindow, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15)),
            MaxPairingFailuresPerWindow = Math.Clamp(MaxPairingFailuresPerWindow, 1, 1_000),
        };
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;
}
