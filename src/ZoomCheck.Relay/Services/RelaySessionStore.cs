using Microsoft.Extensions.Options;
using ZoomCheck.Relay.Contracts;
using ZoomCheck.Relay.Options;

namespace ZoomCheck.Relay.Services;

/// <summary>
/// Single-instance, in-memory relay state. Chosen deliberately: no ciphertext, token, or key
/// material is ever written to disk or to an external store, so a compromised host leaves no
/// durable artifacts and process restart is a full credential reset.
/// </summary>
/// <remarks>
/// All mutations happen under one lock. Operations are O(1)-ish and hold the lock only for
/// bookkeeping, which keeps contention negligible at the intended scale (hundreds of sessions,
/// low-frequency polling) and removes whole classes of race conditions around single-use codes.
/// </remarks>
public sealed class RelaySessionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RelaySession> _sessionsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionIdByPairingCodeHash = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly RelayOptions _options;

    public RelaySessionStore(IOptions<RelayOptions> options, TimeProvider timeProvider)
    {
        _options = (options?.Value ?? new RelayOptions()).Normalized();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RelayOptions Options => _options;

    public int ActiveSessionCount
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired(_timeProvider.GetUtcNow());
                return _sessionsById.Count;
            }
        }
    }

    /// <summary>
    /// Registers a desktop-side pairing. The caller-supplied code is reserved until redemption
    /// or expiry; the relay never reuses a live code across sessions.
    /// </summary>
    public RelayResult<CreatePairingResponse> CreatePairing(CreatePairingRequest? request)
    {
        if (!RelayValidation.IsPairingCode(request?.PairingCode))
        {
            return RelayResult<CreatePairingResponse>.Fail(RelayStatus.Invalid, "invalid_pairing_code");
        }

        if (!RelayValidation.IsPublicKey(request!.DesktopPublicKey))
        {
            return RelayResult<CreatePairingResponse>.Fail(RelayStatus.Invalid, "invalid_public_key");
        }

        var now = _timeProvider.GetUtcNow();
        var desktopToken = RelaySecrets.CreateToken();
        var codeHash = RelaySecrets.HashPairingCode(request!.PairingCode!);

        lock (_gate)
        {
            PurgeExpired(now);

            if (_sessionsById.Count >= _options.MaxSessions)
            {
                return RelayResult<CreatePairingResponse>.Fail(RelayStatus.CapacityExceeded, "relay_at_capacity");
            }

            if (_sessionIdByPairingCodeHash.ContainsKey(codeHash))
            {
                return RelayResult<CreatePairingResponse>.Fail(RelayStatus.CodeConflict, "pairing_code_in_use");
            }

            var session = new RelaySession
            {
                SessionId = RelaySecrets.CreateSessionId(),
                PairingCodeHash = codeHash,
                DesktopTokenHash = RelaySecrets.HashToken(desktopToken),
                DesktopPublicKey = request.DesktopPublicKey!,
                Salt = RelaySecrets.CreateSalt(),
                PairingCodeExpiresAt = now + _options.PairingCodeLifetime,
                ExpiresAt = now + _options.PairingCodeLifetime,
            };

            _sessionsById[session.SessionId] = session;
            _sessionIdByPairingCodeHash[session.PairingCodeHash] = session.SessionId;

            return RelayResult<CreatePairingResponse>.Ok(new CreatePairingResponse(
                session.SessionId,
                desktopToken,
                session.Salt,
                session.ExpiresAt));
        }
    }

    /// <summary>
    /// Redeems a pairing code exactly once. On success the code is immediately released so a
    /// stolen or shoulder-surfed code cannot attach a second companion.
    /// </summary>
    public RelayResult<RedeemPairingResponse> RedeemPairing(RedeemPairingRequest? request)
    {
        if (!RelayValidation.IsPairingCode(request?.PairingCode))
        {
            return RelayResult<RedeemPairingResponse>.Fail(RelayStatus.Invalid, "invalid_pairing_code");
        }

        if (!RelayValidation.IsPublicKey(request!.CompanionPublicKey))
        {
            return RelayResult<RedeemPairingResponse>.Fail(RelayStatus.Invalid, "invalid_public_key");
        }

        var now = _timeProvider.GetUtcNow();
        var companionToken = RelaySecrets.CreateToken();
        var codeHash = RelaySecrets.HashPairingCode(request!.PairingCode!);

        lock (_gate)
        {
            PurgeExpired(now);

            if (!_sessionIdByPairingCodeHash.TryGetValue(codeHash, out var sessionId)
                || !_sessionsById.TryGetValue(sessionId, out var session))
            {
                return RelayResult<RedeemPairingResponse>.Fail(RelayStatus.NotFound, "pairing_code_not_found");
            }

            if (session.PairingCodeExpiresAt is null || session.PairingCodeExpiresAt <= now)
            {
                return RelayResult<RedeemPairingResponse>.Fail(RelayStatus.NotFound, "pairing_code_not_found");
            }

            // Single-use: consume the code and drop the reservation before returning.
            session.PairingCodeExpiresAt = null;
            _sessionIdByPairingCodeHash.Remove(codeHash);

            session.CompanionTokenHash = RelaySecrets.HashToken(companionToken);
            session.CompanionPublicKey = request.CompanionPublicKey;
            session.ExpiresAt = now + _options.SessionIdleTimeout;

            return RelayResult<RedeemPairingResponse>.Ok(new RedeemPairingResponse(
                session.SessionId,
                companionToken,
                session.DesktopPublicKey,
                session.Salt,
                session.ExpiresAt));
        }
    }

    /// <summary>Enqueues an envelope for the peer of <paramref name="sender"/>.</summary>
    public RelayResult<RelayAcceptedResponse> Publish(
        string? sessionId,
        RelayParticipant sender,
        string? bearerToken,
        RelayEnvelope? envelope)
    {
        var problem = RelayValidation.DescribeEnvelopeProblem(envelope);
        if (problem is not null)
        {
            return RelayResult<RelayAcceptedResponse>.Fail(RelayStatus.Invalid, problem);
        }

        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            var auth = Authenticate(sessionId, sender, bearerToken, now);
            if (auth.Status != RelayStatus.Ok)
            {
                return RelayResult<RelayAcceptedResponse>.Fail(auth.Status, auth.Reason);
            }

            var session = auth.Value!;

            // Replay and reordering protection: strictly increasing, per-direction.
            if (envelope!.Sequence <= session.LastSequenceFor(sender))
            {
                return RelayResult<RelayAcceptedResponse>.Fail(RelayStatus.SequenceRejected, "sequence_not_monotonic");
            }

            var queue = session.QueueFor(sender.Peer());
            if (queue.Count >= _options.MaxQueuedMessagesPerDirection)
            {
                // Backpressure instead of dropping: a slow peer must not lose data silently.
                return RelayResult<RelayAcceptedResponse>.Fail(RelayStatus.QueueFull, "queue_full");
            }

            queue.Enqueue(envelope);
            session.SetLastSequence(sender, envelope.Sequence);
            session.ExpiresAt = now + _options.SessionIdleTimeout;

            return RelayResult<RelayAcceptedResponse>.Ok(new RelayAcceptedResponse(envelope.Sequence, session.ExpiresAt));
        }
    }

    /// <summary>
    /// Drains envelopes destined for <paramref name="reader"/> with a sequence greater than
    /// <paramref name="afterSequence"/>. Acknowledged envelopes are removed, so the relay holds
    /// ciphertext for the shortest time that still allows at-least-once delivery.
    /// </summary>
    public RelayResult<RelayMessagesResponse> Poll(
        string? sessionId,
        RelayParticipant reader,
        string? bearerToken,
        long afterSequence)
    {
        if (afterSequence < 0)
        {
            return RelayResult<RelayMessagesResponse>.Fail(RelayStatus.Invalid, "invalid_after_sequence");
        }

        var now = _timeProvider.GetUtcNow();

        lock (_gate)
        {
            var auth = Authenticate(sessionId, reader, bearerToken, now);
            if (auth.Status != RelayStatus.Ok)
            {
                return RelayResult<RelayMessagesResponse>.Fail(auth.Status, auth.Reason);
            }

            var session = auth.Value!;
            var queue = session.QueueFor(reader);

            // Everything at or below the watermark is confirmed delivered; discard it.
            while (queue.Count > 0 && queue.Peek().Sequence <= afterSequence)
            {
                queue.Dequeue();
            }

            var batch = new List<RelayEnvelope>(Math.Min(queue.Count, RelayLimits.MaxPollBatchSize));
            foreach (var envelope in queue)
            {
                if (batch.Count >= RelayLimits.MaxPollBatchSize)
                {
                    break;
                }

                batch.Add(envelope);
            }

            // Before redemption the pairing code remains the only useful handle. Desktop
            // polling must not turn a ten-minute abandoned pairing into a four-hour orphan.
            session.ExpiresAt = session.Connected
                ? now + _options.SessionIdleTimeout
                : session.PairingCodeExpiresAt ?? session.ExpiresAt;

            return RelayResult<RelayMessagesResponse>.Ok(new RelayMessagesResponse(
                session.Connected,
                session.PublicKeyFor(reader.Peer()),
                session.ExpiresAt,
                batch));
        }
    }

    /// <summary>Test and diagnostics helper: forces eviction of timed-out state.</summary>
    public int PurgeExpiredSessions()
    {
        lock (_gate)
        {
            return PurgeExpired(_timeProvider.GetUtcNow());
        }
    }

    private RelayResult<RelaySession> Authenticate(
        string? sessionId,
        RelayParticipant participant,
        string? bearerToken,
        DateTimeOffset now)
    {
        PurgeExpired(now);

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(bearerToken))
        {
            return RelayResult<RelaySession>.Fail(RelayStatus.Unauthorized, "unauthorized");
        }

        if (!_sessionsById.TryGetValue(sessionId, out var session))
        {
            // Deliberately indistinguishable from a bad token: an attacker probing session ids
            // learns nothing about which ids exist.
            return RelayResult<RelaySession>.Fail(RelayStatus.Unauthorized, "unauthorized");
        }

        var expectedHash = session.TokenHashFor(participant);
        if (expectedHash is null || !RelaySecrets.TokenMatches(expectedHash, bearerToken))
        {
            return RelayResult<RelaySession>.Fail(RelayStatus.Unauthorized, "unauthorized");
        }

        return RelayResult<RelaySession>.Ok(session);
    }

    private int PurgeExpired(DateTimeOffset now)
    {
        List<string>? expiredSessionIds = null;
        foreach (var (id, session) in _sessionsById)
        {
            if (session.ExpiresAt <= now)
            {
                (expiredSessionIds ??= new List<string>()).Add(id);
            }
            else if (session.PairingCodeExpiresAt is { } codeExpiry && codeExpiry <= now)
            {
                session.PairingCodeExpiresAt = null;
                _sessionIdByPairingCodeHash.Remove(session.PairingCodeHash);
            }
        }

        if (expiredSessionIds is null)
        {
            return 0;
        }

        foreach (var id in expiredSessionIds)
        {
            if (_sessionsById.Remove(id, out var session))
            {
                _sessionIdByPairingCodeHash.Remove(session.PairingCodeHash);
                session.ToDesktop.Clear();
                session.ToCompanion.Clear();
            }
        }

        return expiredSessionIds.Count;
    }
}
