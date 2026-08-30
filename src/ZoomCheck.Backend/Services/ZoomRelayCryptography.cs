using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Backend.Services;

internal static class ZoomRelayCryptography
{
    private const int TagSize = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ECDiffieHellman CreateKeyPair()
        => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static string ExportPublicKey(ECDiffieHellman keyPair)
        => Convert.ToBase64String(keyPair.ExportSubjectPublicKeyInfo());

    public static RelayKeys DeriveKeys(
        ECDiffieHellman localKeyPair,
        string peerPublicKey,
        string salt,
        string sessionId)
    {
        var publicKeyBytes = Convert.FromBase64String(peerPublicKey);
        var saltBytes = Convert.FromBase64String(salt);
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(publicKeyBytes, out var bytesRead);
        if (bytesRead != publicKeyBytes.Length)
        {
            throw new CryptographicException("The relay peer public key is invalid.");
        }

        var secret = localKeyPair.DeriveRawSecretAgreement(peer.PublicKey);
        try
        {
            return new RelayKeys(
                Derive(secret, saltBytes, Info(sessionId, RelayDirection.CompanionToDesktop)),
                Derive(secret, saltBytes, Info(sessionId, RelayDirection.DesktopToCompanion)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static RelayEnvelope Encrypt<T>(
        T payload,
        byte[] key,
        string sessionId,
        RelayDirection direction,
        long sequence,
        string messageType)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(RelayLimits.NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(sessionId, direction, sequence, messageType));
            var combined = new byte[ciphertext.Length + tag.Length];
            Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);
            return new RelayEnvelope(
                RelayLimits.EnvelopeVersion,
                sequence,
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(combined),
                messageType);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static T Decrypt<T>(
        RelayEnvelope envelope,
        byte[] key,
        string sessionId,
        RelayDirection direction)
    {
        if (envelope.Version != RelayLimits.EnvelopeVersion || envelope.Sequence <= 0
            || string.IsNullOrWhiteSpace(envelope.MessageType))
        {
            throw new CryptographicException("The relay envelope metadata is invalid.");
        }

        var nonce = Convert.FromBase64String(envelope.Nonce ?? string.Empty);
        var combined = Convert.FromBase64String(envelope.Ciphertext ?? string.Empty);
        if (nonce.Length != RelayLimits.NonceBytes || combined.Length <= TagSize)
        {
            throw new CryptographicException("The relay envelope is invalid.");
        }

        var ciphertextLength = combined.Length - TagSize;
        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            nonce,
            combined.AsSpan(0, ciphertextLength),
            combined.AsSpan(ciphertextLength, TagSize),
            plaintext,
            Aad(sessionId, direction, envelope.Sequence, envelope.MessageType));

        try
        {
            return JsonSerializer.Deserialize<T>(plaintext, JsonOptions)
                ?? throw new CryptographicException("The encrypted relay payload is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] Derive(byte[] secret, byte[] salt, byte[] info)
    {
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, key, salt, info);
        return key;
    }

    private static byte[] Info(string sessionId, RelayDirection direction)
        => Encoding.UTF8.GetBytes($"zoomcheck-relay/v1/{sessionId}/{DirectionName(direction)}");

    private static byte[] Aad(string sessionId, RelayDirection direction, long sequence, string messageType)
        => Encoding.UTF8.GetBytes(
            $"zoomcheck-relay|v1|{sessionId}|{DirectionName(direction)}|{sequence}|{messageType}");

    private static string DirectionName(RelayDirection direction)
        => direction == RelayDirection.CompanionToDesktop
            ? "companion-to-desktop"
            : "desktop-to-companion";
}

internal enum RelayDirection
{
    CompanionToDesktop,
    DesktopToCompanion
}

internal sealed class RelayKeys : IDisposable
{
    public RelayKeys(byte[] companionToDesktop, byte[] desktopToCompanion)
    {
        CompanionToDesktop = companionToDesktop;
        DesktopToCompanion = desktopToCompanion;
    }

    public byte[] CompanionToDesktop { get; }
    public byte[] DesktopToCompanion { get; }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(CompanionToDesktop);
        CryptographicOperations.ZeroMemory(DesktopToCompanion);
    }
}
