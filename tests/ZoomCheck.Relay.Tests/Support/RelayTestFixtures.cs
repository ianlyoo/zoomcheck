using System.Security.Cryptography;
using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Relay.Tests.Support;

/// <summary>Helpers producing wire-valid values so tests assert on behaviour, not on encoding.</summary>
public static class RelayTestFixtures
{
    /// <summary>A real exported P-256 SPKI key, since the relay must accept genuine client output.</summary>
    public static string PublicKey()
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(ecdh.PublicKey.ExportSubjectPublicKeyInfo());
    }

    public static string Nonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(RelayLimits.NonceBytes));

    public static string Ciphertext(int bytes = 48) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));

    public static RelayEnvelope Envelope(long sequence, string messageType = "roster.snapshot")
        => new(RelayLimits.EnvelopeVersion, sequence, Nonce(), Ciphertext(), messageType);
}
