using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;
using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomRelayService : IDisposable
{
    private readonly object _gate = new();
    private readonly ZoomRelayClient _client;
    private readonly ZoomAppBridgeService _bridge;
    private readonly ZoomRelayOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private RelaySession? _session;

    public ZoomRelayService(
        ZoomRelayClient client,
        ZoomAppBridgeService bridge,
        IOptions<ZoomRelayOptions> options,
        TimeProvider timeProvider)
    {
        _client = client;
        _bridge = bridge;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public bool IsConfigured => _client.IsConfigured;

    public async Task<ZoomAppPairingCodeResponse> CreatePairingCodeAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new ZoomRelayException("The fixed ZoomCheck relay is not configured in this build.");
        }

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
                var keyPair = ZoomRelayCryptography.CreateKeyPair();
                try
                {
                    var result = await _client.RegisterAsync(
                        new CreatePairingRequest(code, ZoomRelayCryptography.ExportPublicKey(keyPair)),
                        cancellationToken);
                    var created = new RelaySession(
                        result.SessionId, result.DesktopToken, result.Salt, result.ExpiresAt, keyPair);
                    ReplaceSession(created);
                    return new ZoomAppPairingCodeResponse(
                        code, result.ExpiresAt, _client.CompanionUrl()?.AbsoluteUri);
                }
                catch (ZoomRelayException) when (attempt < 4)
                {
                    keyPair.Dispose();
                }
                catch
                {
                    keyPair.Dispose();
                    throw;
                }
            }

            throw new ZoomRelayException("Could not reserve a unique relay pairing code. Try again.");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task<long> RequestSyncAsync(CancellationToken cancellationToken = default)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            RelaySession session;
            long sequence;
            lock (_gate)
            {
                session = RequireConnectedSession();
                sequence = ++session.SendSequence;
            }

            var envelope = ZoomRelayCryptography.Encrypt(
                new { requestedAt = _timeProvider.GetUtcNow() },
                session.Keys!.DesktopToCompanion,
                session.Id,
                RelayDirection.DesktopToCompanion,
                sequence,
                "sync-request");
            await _client.SendDesktopAsync(session.Id, session.Token, envelope, cancellationToken);
            return sequence;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public ZoomAppBridgeStatus GetStatus()
    {
        lock (_gate)
        {
            if (_session is null || _session.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                return EmptyStatus();
            }

            var onlineWindow = TimeSpan.FromSeconds(Math.Clamp(_options.OfflineSeconds, 5, 120));
            return new ZoomAppBridgeStatus(
                _session.Keys is not null
                    && _session.LastSeenAt is not null
                    && _timeProvider.GetUtcNow() - _session.LastSeenAt <= onlineWindow,
                _session.MeetingId,
                _session.MeetingUuid,
                _session.Role,
                _session.ConnectedAt,
                _session.LastSeenAt,
                _session.LastSnapshotAt,
                _session.ActiveParticipants,
                _session.SendSequence,
                _client.CompanionUrl()?.AbsoluteUri,
                _session.Keys is null ? _session.ExpiresAt : null,
                "relay");
        }
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            RelaySession? session;
            lock (_gate)
            {
                session = _session;
            }
            if (session is null || session.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                return;
            }

            var response = await _client.PollDesktopAsync(
                session.Id, session.Token, session.LastReceivedSequence, cancellationToken);
            session.ExpiresAt = response.ExpiresAt;
            if (response.Connected && session.Keys is null && !string.IsNullOrWhiteSpace(response.PeerPublicKey))
            {
                session.Keys = ZoomRelayCryptography.DeriveKeys(
                    session.KeyPair, response.PeerPublicKey, session.Salt, session.Id);
                session.ConnectedAt = _timeProvider.GetUtcNow();
            }
            if (session.Keys is null)
            {
                return;
            }

            foreach (var envelope in response.Messages.OrderBy(item => item.Sequence))
            {
                if (envelope.Sequence <= session.LastReceivedSequence)
                {
                    continue;
                }

                if (string.Equals(envelope.MessageType, "participant-snapshot", StringComparison.Ordinal))
                {
                    var payload = ZoomRelayCryptography.Decrypt<ZoomRelaySnapshotPayload>(
                        envelope, session.Keys.CompanionToDesktop, session.Id, RelayDirection.CompanionToDesktop);
                    // Once AES-GCM authentication succeeds this sequence is consumed, even if the
                    // business payload is rejected. Otherwise one malformed/first-empty message can
                    // poison the queue and be replayed forever instead of waiting for a new snapshot.
                    session.LastReceivedSequence = envelope.Sequence;
                    var result = await _bridge.ApplyRelaySnapshotAsync(payload, cancellationToken);
                    session.LastSeenAt = _timeProvider.GetUtcNow();
                    session.MeetingId = payload.MeetingId;
                    session.MeetingUuid = payload.MeetingUuid;
                    session.Role = NormalizeRole(payload.Role);
                    session.LastSnapshotAt = payload.CapturedAt ?? _timeProvider.GetUtcNow();
                    session.ActiveParticipants = result.ActiveParticipants;
                }
                else if (string.Equals(envelope.MessageType, "heartbeat", StringComparison.Ordinal))
                {
                    var payload = ZoomRelayCryptography.Decrypt<ZoomRelayHeartbeatPayload>(
                        envelope, session.Keys.CompanionToDesktop, session.Id, RelayDirection.CompanionToDesktop);
                    session.LastReceivedSequence = envelope.Sequence;
                    var validated = _bridge.ValidateRelayHeartbeat(payload);
                    session.MeetingId = validated.MeetingId;
                    session.MeetingUuid = payload.MeetingUuid;
                    session.Role = validated.Role;
                    session.LastSeenAt = _timeProvider.GetUtcNow();
                }
                else
                {
                    // Authenticate and acknowledge forward-compatible messages, but they do not
                    // establish participant-source liveness on their own.
                    _ = ZoomRelayCryptography.Decrypt<System.Text.Json.JsonElement>(
                        envelope, session.Keys.CompanionToDesktop, session.Id, RelayDirection.CompanionToDesktop);
                    session.LastReceivedSequence = envelope.Sequence;
                }
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private RelaySession RequireConnectedSession()
    {
        if (_session is null || _session.ExpiresAt <= _timeProvider.GetUtcNow() || _session.Keys is null)
        {
            throw new ZoomAppBridgeException(
                ZoomAppBridgeError.NotConnected,
                "Open ZoomCheck inside the current Zoom meeting and enter a new pairing code.");
        }
        return _session;
    }

    private ZoomAppBridgeStatus EmptyStatus()
        => new(false, null, null, null, null, null, null, 0, 0,
            _client.CompanionUrl()?.AbsoluteUri, null, "relay");

    private static string? NormalizeRole(string? role)
        => string.Equals(role, "host", StringComparison.OrdinalIgnoreCase) ? "host"
            : string.Equals(role, "cohost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "co-host", StringComparison.OrdinalIgnoreCase) ? "coHost"
            : role;

    private void ReplaceSession(RelaySession session)
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = session;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
        _sessionGate.Dispose();
    }

    private sealed class RelaySession : IDisposable
    {
        public RelaySession(
            string id, string token, string salt, DateTimeOffset expiresAt, ECDiffieHellman keyPair)
        {
            Id = id; Token = token; Salt = salt; ExpiresAt = expiresAt; KeyPair = keyPair;
        }

        public string Id { get; }
        public string Token { get; }
        public string Salt { get; }
        public DateTimeOffset ExpiresAt { get; set; }
        public ECDiffieHellman KeyPair { get; }
        public RelayKeys? Keys { get; set; }
        public long LastReceivedSequence { get; set; }
        public long SendSequence { get; set; }
        public DateTimeOffset? ConnectedAt { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
        public DateTimeOffset? LastSnapshotAt { get; set; }
        public string? MeetingId { get; set; }
        public string? MeetingUuid { get; set; }
        public string? Role { get; set; }
        public int ActiveParticipants { get; set; }

        public void Dispose()
        {
            Keys?.Dispose();
            KeyPair.Dispose();
        }
    }
}
