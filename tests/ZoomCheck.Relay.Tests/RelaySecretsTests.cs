using ZoomCheck.Relay.Contracts;
using ZoomCheck.Relay.Services;

namespace ZoomCheck.Relay.Tests;

public sealed class RelaySecretsTests
{
    [Fact]
    public void TokensAreUniqueAcrossManyDraws()
    {
        var tokens = Enumerable.Range(0, 2_000).Select(_ => RelaySecrets.CreateToken()).ToList();

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TokensAreUrlSafe()
    {
        var token = RelaySecrets.CreateToken();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void HashIsStableAndVerifiable()
    {
        var token = RelaySecrets.CreateToken();
        var hash = RelaySecrets.HashToken(token);

        Assert.Equal(32, hash.Length);
        Assert.True(RelaySecrets.TokenMatches(hash, token));
        Assert.False(RelaySecrets.TokenMatches(hash, token + "x"));
        Assert.False(RelaySecrets.TokenMatches(hash, RelaySecrets.CreateToken()));
    }

    [Fact]
    public void HashDoesNotRevealToken()
    {
        var token = RelaySecrets.CreateToken();

        Assert.DoesNotContain(token, Convert.ToBase64String(RelaySecrets.HashToken(token)));
    }

    [Fact]
    public void SaltIsThirtyTwoBytesAndRandom()
    {
        var first = RelaySecrets.CreateSalt();
        var second = RelaySecrets.CreateSalt();

        Assert.Equal(RelayLimits.SaltBytes, Convert.FromBase64String(first).Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PairingCodesAreAlwaysSixDigitsAndCoverFullRange()
    {
        var codes = Enumerable.Range(0, 5_000).Select(_ => RelaySecrets.CreatePairingCode()).ToList();

        Assert.All(codes, code => Assert.True(RelayValidation.IsPairingCode(code)));
        // Leading-zero codes must be representable, which a naive int formatting would drop.
        Assert.True(codes.Distinct().Count() > 4_000);
    }

    [Fact]
    public void PairingCodeHashDoesNotRetainPlaintextCode()
    {
        const string code = "012345";
        var hash = RelaySecrets.HashPairingCode(code);

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(code, hash, StringComparison.Ordinal);
        Assert.Equal(hash, RelaySecrets.HashPairingCode(code));
    }
}
