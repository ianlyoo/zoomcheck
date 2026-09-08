namespace ZoomCheck.Relay.Tests.Support;

/// <summary>
/// Manually advanced clock. Hand-rolled instead of pulling in a testing package so the relay
/// slice keeps its dependency footprint to the packages already used elsewhere in the repo.
/// </summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset? start = null)
        => _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
