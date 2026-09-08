using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomWebhookValidator
{
    private const string EndpointValidationEventName = "endpoint.url_validation";

    // DateTimeOffset.FromUnixTimeSeconds throws outside this range, so malformed
    // headers are range-checked before conversion instead of being allowed to throw.
    private static readonly long MinUnixSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();
    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

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

        if (!long.TryParse(timestampHeader, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            || unixSeconds < MinUnixSeconds
            || unixSeconds > MaxUnixSeconds)
        {
            failureReason = "Invalid Zoom webhook timestamp header.";
            return false;
        }

        try
        {
            var timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            var drift = Math.Abs((DateTimeOffset.UtcNow - timestamp).TotalSeconds);
            if (double.IsNaN(drift) || drift > _options.RequestTimestampToleranceSeconds)
            {
                failureReason = "Zoom webhook timestamp is outside the allowed tolerance window.";
                return false;
            }

            var expected = BuildSignature(timestampHeader, rawBody ?? string.Empty);
            if (!FixedTimeEquals(expected, signatureHeader))
            {
                failureReason = "Zoom webhook signature did not match the request body.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or EncoderFallbackException)
        {
            // A malformed header must surface as a validation failure, never as an
            // unhandled exception that would escape the webhook endpoint as a 500.
            failureReason = "Zoom webhook signature could not be validated.";
            return false;
        }
    }

    public bool IsEndpointValidation(JsonElement payload, out ZoomEndpointValidationResponse response)
    {
        response = null!;

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetString(payload, "event", out var eventName)
            || !string.Equals(eventName, EndpointValidationEventName, StringComparison.Ordinal))
        {
            return false;
        }

        if (!payload.TryGetProperty("payload", out var payloadNode) || payloadNode.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetString(payloadNode, "plainToken", out var plainToken)
            || string.IsNullOrWhiteSpace(plainToken)
            || !IsConfigured)
        {
            return false;
        }

        response = new ZoomEndpointValidationResponse(plainToken, ComputeHexHmac(plainToken));
        return true;
    }

    private static bool TryGetString(JsonElement parent, string propertyName, out string value)
    {
        value = string.Empty;

        if (!parent.TryGetProperty(propertyName, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = node.GetString() ?? string.Empty;
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

    // Digesting both sides to a fixed 32-byte hash keeps the comparison constant time
    // even when the supplied signature has a different length than the expected one.
    // CryptographicOperations.FixedTimeEquals is only fixed time for equal-length spans.
    private static bool FixedTimeEquals(string left, string right)
    {
        Span<byte> leftHash = stackalloc byte[SHA256.HashSizeInBytes];
        Span<byte> rightHash = stackalloc byte[SHA256.HashSizeInBytes];

        SHA256.HashData(Encoding.UTF8.GetBytes(left), leftHash);
        SHA256.HashData(Encoding.UTF8.GetBytes(right), rightHash);

        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
