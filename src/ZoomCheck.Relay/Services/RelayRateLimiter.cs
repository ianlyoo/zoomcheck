using Microsoft.Extensions.Options;
using ZoomCheck.Relay.Options;

namespace ZoomCheck.Relay.Services;

/// <summary>
/// Fixed-window counter keyed by caller identity. Two independent budgets are tracked: overall
/// request volume, and failed pairing redemptions. The second budget is what makes brute-forcing
/// a six-digit code impractical, since a shared secret that small needs an online guess limit.
/// </summary>
public sealed class RelayRateLimiter
{
    private sealed class Window
    {
        public DateTimeOffset ResetsAt;
        public int Requests;
        public int PairingFailures;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly RelayOptions _options;

    public RelayRateLimiter(IOptions<RelayOptions> options, TimeProvider timeProvider)
    {
        _options = (options?.Value ?? new RelayOptions()).Normalized();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Counts one request against <paramref name="key"/>; false means over budget.</summary>
    public bool TryAcquire(string key)
    {
        lock (_gate)
        {
            var window = Current(key);
            if (window.Requests >= _options.RateLimitRequestsPerWindow)
            {
                return false;
            }

            window.Requests++;
            return true;
        }
    }

    /// <summary>True when the caller still has pairing guesses left in the current window.</summary>
    public bool PairingAttemptAllowed(string key)
    {
        lock (_gate)
        {
            return Current(key).PairingFailures < _options.MaxPairingFailuresPerWindow;
        }
    }

    public void RecordPairingFailure(string key)
    {
        lock (_gate)
        {
            Current(key).PairingFailures++;
        }
    }

    private Window Current(string key)
    {
        var now = _timeProvider.GetUtcNow();

        // Opportunistic sweep keeps the dictionary from growing without bound across many IPs.
        if (_windows.Count > 10_000)
        {
            foreach (var stale in _windows.Where(pair => pair.Value.ResetsAt <= now).Select(pair => pair.Key).ToList())
            {
                _windows.Remove(stale);
            }
        }

        if (!_windows.TryGetValue(key, out var window))
        {
            window = new Window { ResetsAt = now + _options.RateLimitWindow };
            _windows[key] = window;
            return window;
        }

        if (window.ResetsAt <= now)
        {
            window.ResetsAt = now + _options.RateLimitWindow;
            window.Requests = 0;
            window.PairingFailures = 0;
        }

        return window;
    }
}
