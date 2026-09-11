using System.Security.Cryptography;
using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Relay.Services;

/// <summary>
/// Token, salt, and identifier generation plus constant-time verification helpers.
/// Bearer tokens are 32 bytes of CSPRNG output (256-bit) encoded base64url, and are persisted
/// only as SHA-256 hashes so a memory dump of the session table cannot be replayed as credentials.
/// </summary>
public static class RelaySecrets
{
    public const int TokenBytes = 32;

    public static string CreateToken()
        => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    public static string CreateSessionId()
        => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>Relay-generated key derivation salt, returned to both peers as base64.</summary>
    public static string CreateSalt()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(RelayLimits.SaltBytes));

    /// <summary>Uniformly random six-digit code with no modulo bias.</summary>
    public static string CreatePairingCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public static byte[] HashToken(string token)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public static string HashPairingCode(string code)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));

    /// <summary>Constant-time comparison to keep token checks free of timing side channels.</summary>
    public static bool TokenMatches(byte[] expectedHash, string presentedToken)
        => CryptographicOperations.FixedTimeEquals(expectedHash, HashToken(presentedToken));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
