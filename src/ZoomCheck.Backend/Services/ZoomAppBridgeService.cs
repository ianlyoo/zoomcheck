using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomAppBridgeService
{
    // Both connectors feed the same authoritative live source. When the Dashboard API
    // participant id and Zoom Apps participantUUID agree, switching modes preserves
    // connection identity and avoids false leave/join events.
    public const string SnapshotSource = ZoomLiveSyncService.SnapshotSource;
    private const int MaxParticipants = 2000;
    private static readonly string[] RequiredApis =
    {
        "getMeetingParticipants",
        "getMeetingContext",
        "getUserContext"
    };

    private readonly object _gate = new();
    private readonly AttendanceApplicationService _attendance;
    private readonly ZoomAppOptions _options;
    private readonly TimeProvider _timeProvider;
    private PairingState? _pairing;
    private SessionState? _session;
    private int _relayActiveParticipants;
    private DateTimeOffset? _relayEmptySnapshotObservedAt;

    public ZoomAppBridgeService(
        AttendanceApplicationService attendance,
        IOptions<ZoomAppOptions> options,
        TimeProvider timeProvider)
    {
        _attendance = attendance;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public ZoomAppPairingCodeResponse CreatePairingCode()
    {
        var now = _timeProvider.GetUtcNow();
        var lifetime = TimeSpan.FromSeconds(Math.Clamp(_options.PairingCodeLifetimeSeconds, 60, 3600));
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        lock (_gate)
        {
            _pairing = new PairingState(Hash(code), now.Add(lifetime));
        }

        return new ZoomAppPairingCodeResponse(code, now.Add(lifetime), NormalizedHomeUrl());
    }

    public ZoomAppConnectResponse Connect(ZoomAppConnectRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        var code = request.PairingCode?.Trim() ?? string.Empty;
        var meetingId = NormalizeMeetingId(request.MeetingId);
        var role = NormalizeRole(request.Role);
        var supportedApis = request.SupportedApis ?? Array.Empty<string>();
        var missingApis = RequiredApis
            .Where(required => !supportedApis.Contains(required, StringComparer.Ordinal))
            .ToArray();

        if (missingApis.Length > 0)
        {
            throw new ZoomAppBridgeException(
                ZoomAppBridgeError.UnsupportedClient,
                $"Zoom client did not expose required SDK APIs: {string.Join(", ", missingApis)}.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var token = WebEncoders.Base64UrlEncode(tokenBytes);
        var tokenHash = SHA256.HashData(tokenBytes);
        var expiresAt = now.AddHours(16);

        lock (_gate)
        {
            if (_pairing is null || _pairing.ExpiresAt <= now)
            {
                throw new ZoomAppBridgeException(
                    ZoomAppBridgeError.InvalidPairingCode,
                    "The pairing code is invalid or expired. Create a new code in the ZoomCheck dashboard.");
            }

            if (!FixedTimeEquals(_pairing.CodeHash, Hash(code)))
            {
                _pairing = _pairing with { FailedAttempts = _pairing.FailedAttempts + 1 };
                if (_pairing.FailedAttempts >= 8)
                {
                    _pairing = null;
                }

                throw new ZoomAppBridgeException(
                    ZoomAppBridgeError.InvalidPairingCode,
                    "The pairing code is invalid or expired. Create a new code in the ZoomCheck dashboard.");
            }

            _pairing = null; // Pairing codes are single-use.
            _session = new SessionState(
                tokenHash, meetingId, request.MeetingUuid?.Trim(), role, now, now, null,
                ActiveParticipants: 0, SyncRevision: 1, ExpiresAt: expiresAt, EmptySnapshotObservedAt: null);
        }

        return new ZoomAppConnectResponse(token, 1, expiresAt);
    }

    public async Task<ZoomAppSnapshotResponse> ApplySnapshotAsync(
        ZoomAppSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var meetingId = NormalizeMeetingId(request.MeetingId);
        var participants = request.Participants
            ?? throw new ZoomAppBridgeException(ZoomAppBridgeError.InvalidRequest, "participants is required.");

        if (participants.Count > MaxParticipants)
        {
            throw new ZoomAppBridgeException(
                ZoomAppBridgeError.InvalidRequest,
                $"A snapshot cannot contain more than {MaxParticipants} participants.");
        }

        var tokenHash = ValidateSession(request.SessionToken, meetingId, now, touch: false);
        var connections = participants
            .Where(item => !string.IsNullOrWhiteSpace(item.ParticipantUuid) && !string.IsNullOrWhiteSpace(item.ScreenName))
            .GroupBy(item => item.ParticipantUuid!.Trim(), StringComparer.Ordinal)
            .Select(group => group.Last())
            .Select(item => new ParticipantSnapshotParticipant(
                $"zoom-app:{item.ParticipantUuid!.Trim()}",
                item.ScreenName!.Trim(),
                Email: null))
            .ToArray();
        var capturedAt = request.CapturedAt ?? now;

        ConfirmEmptySnapshot(tokenHash, connections.Length, now);

        var snapshot = await _attendance.ApplyParticipantSnapshotAsync(
            new ParticipantSnapshotInput(
                meetingId,
                connections.Select(item => item.DisplayName).ToArray(),
                SnapshotSource,
                capturedAt,
                ParticipantEmails: null,
                Participants: connections),
            cancellationToken);

        lock (_gate)
        {
            if (_session is not null && FixedTimeEquals(_session.TokenHash, tokenHash))
            {
                _session = _session with
                {
                    LastSeenAt = now,
                    LastSnapshotAt = capturedAt,
                    ActiveParticipants = connections.Length,
                    EmptySnapshotObservedAt = null
                };
            }
        }

        return new ZoomAppSnapshotResponse(connections.Length, snapshot);
    }

    /// <summary>
    /// Applies a snapshot whose authenticity and confidentiality were established by the
    /// end-to-end encrypted relay session. The relay itself never sees this plaintext.
    /// Zoom's role value is still client-supplied, so it is validated here but is not an
    /// independent server-side attestation.
    /// </summary>
    public async Task<ZoomAppSnapshotResponse> ApplyRelaySnapshotAsync(
        ZoomRelaySnapshotPayload request,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var meetingId = NormalizeMeetingId(request.MeetingId);
        _ = NormalizeRole(request.Role);
        ValidateSupportedApis(request.SupportedApis);

        var participants = request.Participants
            ?? throw new ZoomAppBridgeException(ZoomAppBridgeError.InvalidRequest, "participants is required.");
        if (participants.Count > MaxParticipants)
        {
            throw new ZoomAppBridgeException(
                ZoomAppBridgeError.InvalidRequest,
                $"A snapshot cannot contain more than {MaxParticipants} participants.");
        }

        var connections = ToConnections(participants);
        ConfirmRelayEmptySnapshot(connections.Length, now);
        var capturedAt = request.CapturedAt ?? now;
        var snapshot = await ApplyConnectionsAsync(meetingId, capturedAt, connections, cancellationToken);

        lock (_gate)
        {
            _relayActiveParticipants = connections.Length;
            _relayEmptySnapshotObservedAt = null;
        }

        return new ZoomAppSnapshotResponse(connections.Length, snapshot);
    }

    public (string MeetingId, string Role) ValidateRelayHeartbeat(ZoomRelayHeartbeatPayload request)
    {
        var meetingId = NormalizeMeetingId(request.MeetingId);
        var role = NormalizeRole(request.Role);
        ValidateSupportedApis(request.SupportedApis);
        return (meetingId, role);
    }

    public ZoomAppHeartbeatResponse Heartbeat(ZoomAppHeartbeatRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        SessionState session;

        lock (_gate)
        {
            session = RequireSession(request.SessionToken, now);
            session = session with { LastSeenAt = now };
            _session = session;
        }

        return new ZoomAppHeartbeatResponse(
            session.SyncRevision,
            session.SyncRevision > request.LastRevision,
            now);
    }

    public long RequestSync()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_session is null || _session.ExpiresAt <= now || !IsConnected(_session, now))
            {
                throw new ZoomAppBridgeException(
                    ZoomAppBridgeError.NotConnected,
                    "Open the ZoomCheck companion inside the current Zoom meeting and pair it first.");
            }

            _session = _session with { SyncRevision = _session.SyncRevision + 1 };
            return _session.SyncRevision;
        }
    }

    public ZoomAppBridgeStatus GetStatus()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            DateTimeOffset? pairingExpires = _pairing is { ExpiresAt: var expires } && expires > now
                ? expires
                : null;
            if (_session is null || _session.ExpiresAt <= now)
            {
                return EmptyStatus(pairingExpires);
            }

            return new ZoomAppBridgeStatus(
                IsConnected(_session, now),
                _session.MeetingId,
                _session.MeetingUuid,
                _session.Role,
                _session.ConnectedAt,
                _session.LastSeenAt,
                _session.LastSnapshotAt,
                _session.ActiveParticipants,
                _session.SyncRevision,
                NormalizedHomeUrl(),
                pairingExpires,
                SessionActive: true);
        }
    }

    private byte[] ValidateSession(string? token, string meetingId, DateTimeOffset now, bool touch)
    {
        lock (_gate)
        {
            var session = RequireSession(token, now);
            if (!string.Equals(session.MeetingId, meetingId, StringComparison.Ordinal))
            {
                throw new ZoomAppBridgeException(
                    ZoomAppBridgeError.InvalidRequest,
                    "The snapshot meeting id does not match the paired Zoom meeting.");
            }

            if (touch)
            {
                _session = session with { LastSeenAt = now };
            }

            return session.TokenHash;
        }
    }

    private void ConfirmEmptySnapshot(byte[] tokenHash, int participantCount, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_session is null || !FixedTimeEquals(_session.TokenHash, tokenHash))
            {
                throw new ZoomAppBridgeException(ZoomAppBridgeError.InvalidSession, "The Zoom App bridge session is invalid.");
            }

            if (participantCount > 0 || _session.ActiveParticipants == 0)
            {
                _session = _session with { EmptySnapshotObservedAt = null };
                return;
            }

            var confirmationWindow = TimeSpan.FromSeconds(20);
            if (_session.EmptySnapshotObservedAt is null
                || now - _session.EmptySnapshotObservedAt > confirmationWindow)
            {
                _session = _session with { EmptySnapshotObservedAt = now, LastSeenAt = now };
                throw new ZoomAppBridgeException(
                    ZoomAppBridgeError.UnconfirmedEmptySnapshot,
                    "Zoom App returned an empty participant list once. The previous attendance was kept until the next snapshot confirms that everyone left.");
            }
        }
    }

    private void ConfirmRelayEmptySnapshot(int participantCount, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (participantCount > 0 || _relayActiveParticipants == 0)
            {
                _relayEmptySnapshotObservedAt = null;
                return;
            }

            var confirmationWindow = TimeSpan.FromSeconds(20);
            if (_relayEmptySnapshotObservedAt is null
                || now - _relayEmptySnapshotObservedAt > confirmationWindow)
            {
                _relayEmptySnapshotObservedAt = now;
                throw new ZoomAppBridgeException(
                    ZoomAppBridgeError.UnconfirmedEmptySnapshot,
                    "Zoom App returned an empty participant list once. The previous attendance was kept until the next snapshot confirms that everyone left.");
            }
        }
    }

    private static ParticipantSnapshotParticipant[] ToConnections(
        IReadOnlyList<ZoomAppParticipantRequest> participants)
        => participants
            .Where(item => !string.IsNullOrWhiteSpace(item.ParticipantUuid) && !string.IsNullOrWhiteSpace(item.ScreenName))
            .GroupBy(item => item.ParticipantUuid!.Trim(), StringComparer.Ordinal)
            .Select(group => group.Last())
            .Select(item => new ParticipantSnapshotParticipant(
                $"zoom-app:{item.ParticipantUuid!.Trim()}",
                item.ScreenName!.Trim(),
                Email: null))
            .ToArray();

    private Task<ParticipantSnapshotResult> ApplyConnectionsAsync(
        string meetingId,
        DateTimeOffset capturedAt,
        IReadOnlyList<ParticipantSnapshotParticipant> connections,
        CancellationToken cancellationToken)
        => _attendance.ApplyParticipantSnapshotAsync(
            new ParticipantSnapshotInput(
                meetingId,
                connections.Select(item => item.DisplayName).ToArray(),
                SnapshotSource,
                capturedAt,
                ParticipantEmails: null,
                Participants: connections),
            cancellationToken);

    private static void ValidateSupportedApis(IReadOnlyList<string>? supportedApis)
    {
        var supported = supportedApis ?? Array.Empty<string>();
        var missing = RequiredApis
            .Where(required => !supported.Contains(required, StringComparer.Ordinal))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ZoomAppBridgeException(
                ZoomAppBridgeError.UnsupportedClient,
                $"Zoom client did not expose required SDK APIs: {string.Join(", ", missing)}.");
        }
    }

    private SessionState RequireSession(string? token, DateTimeOffset now)
    {
        byte[] supplied;
        try
        {
            supplied = WebEncoders.Base64UrlDecode(token?.Trim() ?? string.Empty);
        }
        catch (FormatException)
        {
            supplied = Array.Empty<byte>();
        }

        var suppliedHash = SHA256.HashData(supplied);
        if (_session is null || _session.ExpiresAt <= now || !FixedTimeEquals(_session.TokenHash, suppliedHash))
        {
            throw new ZoomAppBridgeException(
                ZoomAppBridgeError.InvalidSession,
                "The Zoom App bridge session is invalid or expired. Pair the companion again.");
        }

        return _session;
    }

    private bool IsConnected(SessionState session, DateTimeOffset now)
        => now - session.LastSeenAt <= TimeSpan.FromSeconds(Math.Clamp(_options.SessionOfflineSeconds, 5, 120));

    private ZoomAppBridgeStatus EmptyStatus(DateTimeOffset? pairingExpires)
        => new(false, null, null, null, null, null, null, 0, 0, NormalizedHomeUrl(), pairingExpires);

    private string? NormalizedHomeUrl()
        => Uri.TryCreate(_options.HomeUrl?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;

    private static string NormalizeMeetingId(string? meetingId)
    {
        if (!MeetingIdNormalizer.TryNormalize(meetingId, out var normalized))
        {
            throw new ZoomAppBridgeException(ZoomAppBridgeError.InvalidRequest, "A valid meeting id is required.");
        }
        return normalized;
    }

    private static string NormalizeRole(string? role)
    {
        if (string.Equals(role, "host", StringComparison.OrdinalIgnoreCase))
        {
            return "host";
        }

        if (string.Equals(role, "cohost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "coHost", StringComparison.Ordinal))
        {
            return "coHost";
        }

        throw new ZoomAppBridgeException(
            ZoomAppBridgeError.InsufficientRole,
            "The Zoom App must be opened by the meeting host or a co-host.");
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool FixedTimeEquals(byte[] left, byte[] right)
        => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    private sealed record PairingState(byte[] CodeHash, DateTimeOffset ExpiresAt, int FailedAttempts = 0);

    private sealed record SessionState(
        byte[] TokenHash,
        string MeetingId,
        string? MeetingUuid,
        string Role,
        DateTimeOffset ConnectedAt,
        DateTimeOffset LastSeenAt,
        DateTimeOffset? LastSnapshotAt,
        int ActiveParticipants,
        long SyncRevision,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? EmptySnapshotObservedAt);
}

public enum ZoomAppBridgeError
{
    InvalidRequest,
    InvalidPairingCode,
    InvalidSession,
    InsufficientRole,
    UnsupportedClient,
    NotConnected,
    UnconfirmedEmptySnapshot
}

public sealed class ZoomAppBridgeException : Exception
{
    public ZoomAppBridgeException(ZoomAppBridgeError error, string message) : base(message)
    {
        Error = error;
    }

    public ZoomAppBridgeError Error { get; }
}
