namespace ZoomCheck.Relay.Contracts;

/// <summary>
/// An opaque end-to-end encrypted payload. The relay never has the key material required to
/// read <see cref="Ciphertext"/>; it validates shape and forwards bytes.
/// </summary>
/// <param name="Version">Envelope format version. Must be <see cref="RelayLimits.EnvelopeVersion"/>.</param>
/// <param name="Sequence">Sender-scoped monotonically increasing counter, starting at 1.</param>
/// <param name="Nonce">Base64 AES-GCM nonce, 12 bytes decoded.</param>
/// <param name="Ciphertext">Base64 ciphertext including the authentication tag.</param>
/// <param name="MessageType">Client-defined discriminator, e.g. <c>roster.snapshot</c>.</param>
public sealed record RelayEnvelope(
    int Version,
    long Sequence,
    string? Nonce,
    string? Ciphertext,
    string? MessageType);

/// <summary>Result of a successful poll of a directional queue.</summary>
/// <param name="Connected">True once both desktop and companion have joined the session.</param>
/// <param name="PeerPublicKey">Base64 SPKI key of the other side, or null if it has not joined yet.</param>
/// <param name="Messages">Envelopes with a sequence greater than the requested watermark, oldest first.</param>
public sealed record RelayMessagesResponse(
    bool Connected,
    string? PeerPublicKey,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<RelayEnvelope> Messages);

/// <summary>Acknowledgement for an accepted envelope.</summary>
public sealed record RelayAcceptedResponse(long Sequence, DateTimeOffset ExpiresAt);

/// <summary>Liveness and capacity probe payload.</summary>
public sealed record RelayHealthResponse(
    string Status,
    int ActiveSessions,
    int MaxSessions,
    DateTimeOffset Timestamp);
