namespace ZoomCheck.Relay.Services;

/// <summary>Outcome codes returned by <see cref="RelaySessionStore"/> operations.</summary>
public enum RelayStatus
{
    Ok,

    /// <summary>Input failed shape validation.</summary>
    Invalid,

    /// <summary>Pairing code already taken by a live pairing.</summary>
    CodeConflict,

    /// <summary>Unknown, expired, or already-redeemed pairing code; also unknown session.</summary>
    NotFound,

    /// <summary>Presented bearer token did not match the session's stored hash.</summary>
    Unauthorized,

    /// <summary>Session exists but the peer has not joined yet.</summary>
    PeerAbsent,

    /// <summary>Sequence was not strictly greater than the last accepted sequence.</summary>
    SequenceRejected,

    /// <summary>Directional queue is at its configured cap.</summary>
    QueueFull,

    /// <summary>Relay is at its session cap.</summary>
    CapacityExceeded,
}

/// <summary>Status plus optional payload, avoiding exceptions for expected control flow.</summary>
public readonly record struct RelayResult<T>(RelayStatus Status, T? Value, string? Reason = null)
{
    public bool IsOk => Status == RelayStatus.Ok;

    public static RelayResult<T> Ok(T value) => new(RelayStatus.Ok, value);

    public static RelayResult<T> Fail(RelayStatus status, string? reason = null) => new(status, default, reason);
}
