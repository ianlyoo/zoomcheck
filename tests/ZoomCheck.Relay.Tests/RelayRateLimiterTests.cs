using ZoomCheck.Relay.Options;
using ZoomCheck.Relay.Services;
using ZoomCheck.Relay.Tests.Support;

namespace ZoomCheck.Relay.Tests;

public sealed class RelayRateLimiterTests
{
    private readonly TestClock _clock = new();

    private RelayRateLimiter Create(Action<RelayOptions>? configure = null)
    {
        var options = new RelayOptions();
        configure?.Invoke(options);
        return new RelayRateLimiter(Microsoft.Extensions.Options.Options.Create(options), _clock);
    }

    [Fact]
    public void AllowsUpToBudgetThenBlocks()
    {
        var limiter = Create(o => o.RateLimitRequestsPerWindow = 3);

        Assert.True(limiter.TryAcquire("1.2.3.4"));
        Assert.True(limiter.TryAcquire("1.2.3.4"));
        Assert.True(limiter.TryAcquire("1.2.3.4"));
        Assert.False(limiter.TryAcquire("1.2.3.4"));
    }

    [Fact]
    public void BudgetsAreIndependentPerClient()
    {
        var limiter = Create(o => o.RateLimitRequestsPerWindow = 1);
        Assert.True(limiter.TryAcquire("1.1.1.1"));

        Assert.False(limiter.TryAcquire("1.1.1.1"));
        Assert.True(limiter.TryAcquire("2.2.2.2"));
    }

    [Fact]
    public void WindowResetsAfterElapsing()
    {
        var limiter = Create(o =>
        {
            o.RateLimitRequestsPerWindow = 1;
            o.RateLimitWindow = TimeSpan.FromSeconds(30);
        });
        limiter.TryAcquire("1.1.1.1");
        Assert.False(limiter.TryAcquire("1.1.1.1"));

        _clock.Advance(TimeSpan.FromSeconds(31));

        Assert.True(limiter.TryAcquire("1.1.1.1"));
    }

    [Fact]
    public void PairingGuessesAreBudgetedSeparately()
    {
        var limiter = Create(o => o.MaxPairingFailuresPerWindow = 2);

        Assert.True(limiter.PairingAttemptAllowed("1.1.1.1"));
        limiter.RecordPairingFailure("1.1.1.1");
        Assert.True(limiter.PairingAttemptAllowed("1.1.1.1"));
        limiter.RecordPairingFailure("1.1.1.1");

        Assert.False(limiter.PairingAttemptAllowed("1.1.1.1"));
        Assert.True(limiter.PairingAttemptAllowed("2.2.2.2"));
    }

    [Fact]
    public void PairingFailureBudgetResetsWithWindow()
    {
        var limiter = Create(o =>
        {
            o.MaxPairingFailuresPerWindow = 1;
            o.RateLimitWindow = TimeSpan.FromMinutes(1);
        });
        limiter.RecordPairingFailure("1.1.1.1");
        Assert.False(limiter.PairingAttemptAllowed("1.1.1.1"));

        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.True(limiter.PairingAttemptAllowed("1.1.1.1"));
    }

    [Fact]
    public void ConcurrentAcquisitionNeverExceedsBudget()
    {
        var limiter = Create(o => o.RateLimitRequestsPerWindow = 50);
        var granted = 0;

        Parallel.For(0, 500, _ =>
        {
            if (limiter.TryAcquire("1.1.1.1"))
            {
                Interlocked.Increment(ref granted);
            }
        });

        Assert.Equal(50, granted);
    }
}
