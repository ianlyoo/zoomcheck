namespace ZoomCheck.Relay.Contracts;

/// <summary>
/// Wire-level limits shared by relay clients (desktop app, Zoom companion) and the relay itself.
/// These are part of the public contract so clients can fail fast locally instead of
/// discovering limits through rejected requests.
/// </summary>
public static class RelayLimits
{
    /// <summary>Only envelope version 1 is understood by this relay generation.</summary>
    public const int EnvelopeVersion = 1;

    /// <summary>Pairing codes are exactly six decimal digits, entered by a human.</summary>
    public const int PairingCodeLength = 6;

    /// <summary>AES-GCM nonce length in bytes (96-bit, per NIST SP 800-38D).</summary>
    public const int NonceBytes = 12;

    /// <summary>Relay-generated key-derivation salt length in bytes.</summary>
    public const int SaltBytes = 32;

    /// <summary>Maximum decoded ciphertext size for a single envelope.</summary>
    public const int MaxCiphertextBytes = 512 * 1024;

    /// <summary>Minimum decoded length of an accepted SPKI public key blob.</summary>
    public const int MinPublicKeyBytes = 64;

    /// <summary>Maximum decoded length of an accepted SPKI public key blob.</summary>
    public const int MaxPublicKeyBytes = 256;

    /// <summary>Maximum length of the free-form <c>messageType</c> discriminator.</summary>
    public const int MaxMessageTypeLength = 64;

    /// <summary>Maximum number of envelopes a client may request in one poll.</summary>
    public const int MaxPollBatchSize = 64;
}
