using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomWebhookValidator
{
    private readonly ZoomOptions _options;

    public ZoomWebhookValidator(IOptions<ZoomOptions> options)
    {
        _options = options.Value;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.WebhookSecretToken);

    public bool TryValidate(string rawBody, string? timestampHeader, string? signatureHeader, out string failureReason)
    {
        failureReason = string.Empty;

        if (!IsConfigured)
        {
            failureReason = "Zoom webhook secret is not configured.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(timestampHeader) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            failureReason = "Missing Zoom webhook signature headers.";
            return false;
        }

        if (!long.TryParse(timestampHeader, out var unixSeconds))
        {
            failureReason = "Invalid Zoom webhook timestamp header.";
            return false;
        }

        var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        var drift = Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalSeconds);
        if (drift > _options.RequestTimestampToleranceSeconds)
        {
            failureReason = "Zoom webhook timestamp is outside the allowed tolerance window.";
            return false;
        }

        var expected = BuildSignature(timestampHeader, rawBody);
        if (!FixedTimeEquals(expected, signatureHeader))
        {
            failureReason = "Zoom webhook signature did not match the request body.";
            return false;
        }

        return true;
    }

    public bool IsEndpointValidation(JsonElement payload, out ZoomEndpointValidationResponse response)
    {
        response = null!;
        if (!payload.TryGetProperty("event", out var eventNode) || !string.Equals(eventNode.GetString(), "endpoint.url_validation", StringComparison.Ordinal))
        {
            return false;
        }

        if (!payload.TryGetProperty("payload", out var payloadNode) || !payloadNode.TryGetProperty("plainToken", out var tokenNode))
        {
            return false;
        }

        var plainToken = tokenNode.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(plainToken) || !IsConfigured)
        {
            return false;
        }

        response = new ZoomEndpointValidationResponse(plainToken, ComputeHexHmac(plainToken));
        return true;
    }

    private string BuildSignature(string timestampHeader, string rawBody)
    {
        var message = $"v0:{timestampHeader}:{rawBody}";
        return $"v0={ComputeHexHmac(message)}";
    }

    private string ComputeHexHmac(string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecretToken));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
