using ZoomCheck.Relay.Contracts;
using ZoomCheck.Relay.Services;
using ZoomCheck.Relay.Tests.Support;

namespace ZoomCheck.Relay.Tests;

public sealed class RelayValidationTests
{
    [Fact]
    public void AcceptsGenuineP256SpkiKey()
        => Assert.True(RelayValidation.IsPublicKey(RelayTestFixtures.PublicKey()));

    [Theory]
    [InlineData("000000", true)]
    [InlineData("999999", true)]
    [InlineData("012345", true)]
    [InlineData("+12345", false)]
    [InlineData("1e5000", false)]
    [InlineData(" 12345", false)]
    public void PairingCodeAcceptsOnlySixAsciiDigits(string value, bool expected)
        => Assert.Equal(expected, RelayValidation.IsPairingCode(value));

    [Fact]
    public void NonceMustBeExactlyTwelveBytes()
    {
        Assert.True(RelayValidation.IsNonce(Convert.ToBase64String(new byte[12])));
        Assert.False(RelayValidation.IsNonce(Convert.ToBase64String(new byte[11])));
        Assert.False(RelayValidation.IsNonce(Convert.ToBase64String(new byte[13])));
        Assert.False(RelayValidation.IsNonce("!!!!"));
        Assert.False(RelayValidation.IsNonce(null));
    }

    [Fact]
    public void CiphertextIsBoundedAndNonEmpty()
    {
        Assert.True(RelayValidation.IsCiphertext(Convert.ToBase64String(new byte[1])));
        Assert.True(RelayValidation.IsCiphertext(Convert.ToBase64String(new byte[RelayLimits.MaxCiphertextBytes])));
        Assert.False(RelayValidation.IsCiphertext(Convert.ToBase64String(new byte[RelayLimits.MaxCiphertextBytes + 1])));
        Assert.False(RelayValidation.IsCiphertext(string.Empty));
    }

    [Theory]
    [InlineData("roster.snapshot", true)]
    [InlineData("attendance-update_1", true)]
    [InlineData("", false)]
    [InlineData("bad type", false)]
    [InlineData("bad/type", false)]
    [InlineData("<script>", false)]
    [InlineData("\n", false)]
    public void MessageTypeIsRestrictedToSafeCharacters(string value, bool expected)
        => Assert.Equal(expected, RelayValidation.IsMessageType(value));

    [Fact]
    public void MessageTypeLengthIsBounded()
    {
        Assert.True(RelayValidation.IsMessageType(new string('a', RelayLimits.MaxMessageTypeLength)));
        Assert.False(RelayValidation.IsMessageType(new string('a', RelayLimits.MaxMessageTypeLength + 1)));
    }

    [Fact]
    public void ValidEnvelopeHasNoProblem()
        => Assert.Null(RelayValidation.DescribeEnvelopeProblem(RelayTestFixtures.Envelope(1)));

    [Fact]
    public void NullEnvelopeIsRejected()
        => Assert.Equal("envelope_required", RelayValidation.DescribeEnvelopeProblem(null));

    [Fact]
    public void EnvelopeProblemsAreReportedSpecifically()
    {
        Assert.Equal(
            "invalid_nonce",
            RelayValidation.DescribeEnvelopeProblem(new RelayEnvelope(1, 1, "AAAA", RelayTestFixtures.Ciphertext(), "t")));
        Assert.Equal(
            "invalid_ciphertext",
            RelayValidation.DescribeEnvelopeProblem(new RelayEnvelope(1, 1, RelayTestFixtures.Nonce(), "!!", "t")));
        Assert.Equal(
            "invalid_message_type",
            RelayValidation.DescribeEnvelopeProblem(new RelayEnvelope(1, 1, RelayTestFixtures.Nonce(), RelayTestFixtures.Ciphertext(), "bad type")));
    }
}
