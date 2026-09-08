using System.Diagnostics.CodeAnalysis;
using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Relay.Services;

/// <summary>
/// Pure input validation. Every check is shape-only: the relay deliberately never interprets
/// key material or ciphertext, it only bounds what it is willing to store and forward.
/// </summary>
public static class RelayValidation
{
    public static bool IsPairingCode([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length != RelayLimits.PairingCodeLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Accepts a base64 SPKI blob within P-256-plausible bounds. The bytes stay opaque:
    /// the relay never imports the key, which keeps parser attack surface out of the server.
    /// </summary>
    public static bool IsPublicKey([NotNullWhen(true)] string? value)
        => TryDecodeBase64(value, RelayLimits.MinPublicKeyBytes, RelayLimits.MaxPublicKeyBytes, out _);

    public static bool IsNonce([NotNullWhen(true)] string? value)
        => TryDecodeBase64(value, RelayLimits.NonceBytes, RelayLimits.NonceBytes, out _);

    public static bool IsCiphertext([NotNullWhen(true)] string? value)
        => TryDecodeBase64(value, 1, RelayLimits.MaxCiphertextBytes, out _);

    public static bool IsMessageType([NotNullWhen(true)] string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > RelayLimits.MaxMessageTypeLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var ok = ch is >= 'a' and <= 'z'
                || ch is >= 'A' and <= 'Z'
                || ch is >= '0' and <= '9'
                || ch is '.' or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates an inbound envelope's shape, returning a stable machine-readable reason.</summary>
    public static string? DescribeEnvelopeProblem(RelayEnvelope? envelope)
    {
        if (envelope is null)
        {
            return "envelope_required";
        }

        if (envelope.Version != RelayLimits.EnvelopeVersion)
        {
            return "unsupported_version";
        }

        if (envelope.Sequence <= 0)
        {
            return "invalid_sequence";
        }

        if (!IsNonce(envelope.Nonce))
        {
            return "invalid_nonce";
        }

        if (!IsCiphertext(envelope.Ciphertext))
        {
            return "invalid_ciphertext";
        }

        if (!IsMessageType(envelope.MessageType))
        {
            return "invalid_message_type";
        }

        return null;
    }

    private static bool TryDecodeBase64(string? value, int minBytes, int maxBytes, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Reject oversized inputs before allocating, so a large body cannot force a large decode.
        if ((long)value.Length > ((long)maxBytes + 2) / 3 * 4 + 4)
        {
            return false;
        }

        var buffer = new byte[((value.Length + 3) / 4) * 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return false;
        }

        if (written < minBytes || written > maxBytes)
        {
            return false;
        }

        decoded = buffer[..written];
        return true;
    }
}
