using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Backend.Tests;

/// <summary>
/// Regression coverage for Zoom webhook signature validation.
/// Timestamps are generated relative to <see cref="DateTimeOffset.UtcNow"/> with a
/// deliberately generous tolerance, so only the explicit stale and far-future tests
/// depend on the tolerance window. Nothing here sleeps or races the clock.
/// </summary>
public sealed class ZoomWebhookValidatorTests
{
    private const string Secret = "test-webhook-secret";
    private const int GenerousToleranceSeconds = 86_400;
    private const string SampleBody = @"{""event"":""meeting.participant_joined""}";

    [Fact]
    public void TryValidate_ReturnsTrue_ForCorrectlySignedBody()
    {
        var validator = CreateValidator();
        var timestamp = CurrentTimestamp();

        var result = validator.TryValidate(
            SampleBody,
            timestamp,
            SignatureFor(timestamp, SampleBody),
            out var failureReason);

        Assert.True(result);
        Assert.Equal(string.Empty, failureReason);
    }

    [Fact]
    public void TryValidate_ReturnsFalse_WhenBodyWasTampered()
    {
        var validator = CreateValidator();
        var timestamp = CurrentTimestamp();
        var signature = SignatureFor(timestamp, SampleBody);

        var result = validator.TryValidate(
            @"{""event"":""meeting.participant_left""}",
            timestamp,
            signature,
            out var failureReason);

        Assert.False(result);
        Assert.Equal("Zoom webhook signature did not match the request body.", failureReason);
    }

    [Theory]
    // Unequal byte lengths are the core regression. CryptographicOperations.FixedTimeEquals
    // is only fixed time for equal-length spans, so these must fail validation cleanly.
    [InlineData("v0=")]
    [InlineData("v0=abcd")]
    [InlineData("deadbeef")]
    [InlineData("v0=not-hex-at-all")]
    [InlineData("v0=zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("v0=00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff00")]
    [InlineData("\u1112\u1161\u11abopa")]
    public void TryValidate_ReturnsFalse_ForMalformedOrUnequalLengthSignature(string signatureHeader)
    {
        var validator = CreateValidator();
        var timestamp = CurrentTimestamp();

        var result = validator.TryValidate(SampleBody, timestamp, signatureHeader, out var failureReason);

        Assert.False(result);
        Assert.Equal("Zoom webhook signature did not match the request body.", failureReason);
    }

    [Fact]
    public void TryValidate_DoesNotThrow_ForSignatureOneByteShorterThanExpected()
    {
        var validator = CreateValidator();
        var timestamp = CurrentTimestamp();
        var expected = SignatureFor(timestamp, SampleBody);
        var oneByteShort = expected[..^1];

        var result = validator.TryValidate(SampleBody, timestamp, oneByteShort, out var failureReason);

        Assert.False(result);
        Assert.Equal("Zoom webhook signature did not match the request body.", failureReason);
    }

    [Fact]
    public void TryValidate_DoesNotThrow_ForVeryLongSignatureHeader()
    {
        var validator = CreateValidator();
        var timestamp = CurrentTimestamp();
        var oversized = "v0=" + new string('a', 100_000);

        var result = validator.TryValidate(SampleBody, timestamp, oversized, out var failureReason);

        Assert.False(result);
        Assert.Equal("Zoom webhook signature did not match the request body.", failureReason);
    }

    [Theory]
    [InlineData(null, "v0=abcd")]
    [InlineData("", "v0=abcd")]
    [InlineData("   ", "v0=abcd")]
    [InlineData("1700000000", null)]
    [InlineData("1700000000", "")]
    [InlineData("1700000000", "   ")]
    [InlineData(null, null)]
    public void TryValidate_ReturnsFalse_WhenSignatureHeadersAreMissing(string? timestampHeader, string? signatureHeader)
    {
        var validator = CreateValidator();

        var result = validator.TryValidate(SampleBody, timestampHeader, signatureHeader, out var failureReason);

        Assert.False(result);
        Assert.Equal("Missing Zoom webhook signature headers.", failureReason);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("12.5")]
    [InlineData("0x10")]
    [InlineData("99999999999999999999")]
    [InlineData("-99999999999999999999")]
    // Parses as a long but is outside the DateTimeOffset range, which previously
    // reached FromUnixTimeSeconds and threw ArgumentOutOfRangeException.
    [InlineData("253402300800")]
    [InlineData("-62135596801")]
    [InlineData("9223372036854775807")]
    public void TryValidate_ReturnsFalse_ForUnparsableOrOutOfRangeTimestamp(string timestampHeader)
    {
        var validator = CreateValidator();

        var result = validator.TryValidate(SampleBody, timestampHeader, "v0=abcd", out var failureReason);

        Assert.False(result);
        Assert.Equal("Invalid Zoom webhook timestamp header.", failureReason);
    }

    [Theory]
    // long.TryParse with NumberStyles.Integer tolerates surrounding whitespace, so these
    // parse successfully and are then rejected by the tolerance window, not as malformed.
    // Pinned deliberately: it documents where the parse boundary actually sits.
    [InlineData(" 1700000000")]
    [InlineData("1700000000 ")]
    [InlineData("+1700000000")]
    public void TryValidate_TreatsWhitespacePaddedTimestampAsParsableButStale(string timestampHeader)
    {
        var validator = CreateValidator(toleranceSeconds: 300);

        var result = validator.TryValidate(SampleBody, timestampHeader, "v0=abcd", out var failureReason);

        Assert.False(result);
        Assert.Equal("Zoom webhook timestamp is outside the allowed tolerance window.", failureReason);
    }

    [Fact]
    public void TryValidate_ReturnsFalse_WhenTimestampIsStale()
    {
        // The tolerance window is 300s and the timestamp is an hour old, leaving a
        // 55-minute margin. No sleep, no borderline arithmetic, so it cannot flake.
        var validator = CreateValidator(toleranceSeconds: 300);
        var staleTimestamp = DateTimeOffset.UtcNow
            .AddHours(-1)
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        var result = validator.TryValidate(
            SampleBody,
            staleTimestamp,
            SignatureFor(staleTimestamp, SampleBody),
            out var failureReason);

        Assert.False(result);
        Assert.Equal("Zoom webhook timestamp is outside the allowed tolerance window.", failureReason);
    }

    [Fact]
    public void TryValidate_ReturnsFalse_ForFarFutureTimestamp()
    {
        var validator = CreateValidator(toleranceSeconds: 300);
        var futureTimestamp = DateTimeOffset.UtcNow
            .AddHours(1)
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        var result = validator.TryValidate(
            SampleBody,
            futureTimestamp,
            SignatureFor(futureTimestamp, SampleBody),
            out var failureReason);

        Assert.False(result);
        Assert.Equal("Zoom webhook timestamp is outside the allowed tolerance window.", failureReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_ReturnsFalse_WhenSecretIsNotConfigured(string? secret)
    {
        var validator = CreateValidator(secret);
        var timestamp = CurrentTimestamp();

        var result = validator.TryValidate(SampleBody, timestamp, "v0=abcd", out var failureReason);

        Assert.False(result);
        Assert.Equal("Zoom webhook secret is not configured.", failureReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsConfigured_IsFalse_ForBlankSecret(string? secret)
    {
        Assert.False(CreateValidator(secret).IsConfigured);
    }

    [Fact]
    public void IsConfigured_IsTrue_ForPopulatedSecret()
    {
        Assert.True(CreateValidator().IsConfigured);
    }

    [Fact]
    public void IsEndpointValidation_ReturnsHmacOfPlainToken()
    {
        var validator = CreateValidator();
        var payload = ParseJson(@"{""event"":""endpoint.url_validation"",""payload"":{""plainToken"":""abc123""}}");

        var result = validator.IsEndpointValidation(payload, out var response);

        Assert.True(result);
        Assert.NotNull(response);
        Assert.Equal("abc123", response.PlainToken);
        Assert.Equal(HexHmac(Secret, "abc123"), response.EncryptedToken);
    }

    [Theory]
    // Wrong event name.
    [InlineData(@"{""event"":""meeting.started"",""payload"":{""plainToken"":""abc123""}}")]
    // Missing event property.
    [InlineData(@"{""payload"":{""plainToken"":""abc123""}}")]
    // Missing payload object.
    [InlineData(@"{""event"":""endpoint.url_validation""}")]
    // Missing plainToken.
    [InlineData(@"{""event"":""endpoint.url_validation"",""payload"":{}}")]
    // Blank plainToken.
    [InlineData(@"{""event"":""endpoint.url_validation"",""payload"":{""plainToken"":""   ""}}")]
    // Unexpected JSON types must not throw InvalidOperationException.
    [InlineData(@"{""event"":123,""payload"":{""plainToken"":""abc123""}}")]
    [InlineData(@"{""event"":""endpoint.url_validation"",""payload"":{""plainToken"":42}}")]
    [InlineData(@"{""event"":""endpoint.url_validation"",""payload"":""not-an-object""}")]
    [InlineData(@"{""event"":""endpoint.url_validation"",""payload"":[]}")]
    [InlineData(@"{""event"":null,""payload"":null}")]
    // Non-object roots must not throw.
    [InlineData("[]")]
    [InlineData(@"""just-a-string""")]
    [InlineData("7")]
    [InlineData("null")]
    public void IsEndpointValidation_ReturnsFalse_ForNonValidationPayloads(string json)
    {
        var validator = CreateValidator();

        Assert.False(validator.IsEndpointValidation(ParseJson(json), out _));
    }

    [Fact]
    public void IsEndpointValidation_ReturnsFalse_WhenSecretIsNotConfigured()
    {
        var validator = CreateValidator(secret: null);
        var payload = ParseJson(@"{""event"":""endpoint.url_validation"",""payload"":{""plainToken"":""abc123""}}");

        Assert.False(validator.IsEndpointValidation(payload, out _));
    }

    private static ZoomWebhookValidator CreateValidator(
        string? secret = Secret,
        int toleranceSeconds = GenerousToleranceSeconds)
    {
        var options = new ZoomOptions
        {
            WebhookSecretToken = secret ?? string.Empty,
            RequestTimestampToleranceSeconds = toleranceSeconds
        };

        // Fully qualified: "using ZoomCheck.Backend.Options" makes the unqualified
        // Options.Create bind to that namespace instead of Microsoft.Extensions.Options.
        return new ZoomWebhookValidator(Microsoft.Extensions.Options.Options.Create(options));
    }

    private static string CurrentTimestamp()
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string SignatureFor(string timestamp, string body)
        => $"v0={HexHmac(Secret, $"v0:{timestamp}:{body}")}";

    private static string HexHmac(string secret, string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    // Clone detaches the element from the JsonDocument so it stays valid after disposal.
    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
