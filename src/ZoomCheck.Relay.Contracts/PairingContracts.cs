namespace ZoomCheck.Relay.Contracts;

/// <summary>
/// Desktop-initiated pairing registration. The desktop picks the six-digit code it will
/// display to the operator and supplies its ephemeral P-256 public key.
/// </summary>
/// <param name="PairingCode">Exactly six decimal digits.</param>
/// <param name="DesktopPublicKey">Base64-encoded SPKI public key, treated as opaque by the relay.</param>
public sealed record CreatePairingRequest(string? PairingCode, string? DesktopPublicKey);

/// <param name="SessionId">Opaque relay session identifier.</param>
/// <param name="DesktopToken">High-entropy bearer token for desktop-side endpoints. Returned once, never again.</param>
/// <param name="Salt">Base64 relay-generated salt for client-side key derivation.</param>
/// <param name="ExpiresAt">Instant after which the session is unusable.</param>
public sealed record CreatePairingResponse(
    string SessionId,
    string DesktopToken,
    string Salt,
    DateTimeOffset ExpiresAt);

/// <summary>Companion-side redemption of a pairing code shown on the desktop.</summary>
public sealed record RedeemPairingRequest(string? PairingCode, string? CompanionPublicKey);

/// <param name="DesktopPublicKey">The peer key the companion needs to complete ECDH.</param>
public sealed record RedeemPairingResponse(
    string SessionId,
    string CompanionToken,
    string DesktopPublicKey,
    string Salt,
    DateTimeOffset ExpiresAt);
