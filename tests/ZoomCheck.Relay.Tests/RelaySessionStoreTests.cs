using Microsoft.Extensions.Options;
using ZoomCheck.Relay.Contracts;
using ZoomCheck.Relay.Options;
using ZoomCheck.Relay.Services;
using ZoomCheck.Relay.Tests.Support;

namespace ZoomCheck.Relay.Tests;

public sealed class RelaySessionStoreTests
{
    private readonly TestClock _clock = new();

    private RelaySessionStore CreateStore(Action<RelayOptions>? configure = null)
    {
        var options = new RelayOptions();
        configure?.Invoke(options);
        return new RelaySessionStore(Microsoft.Extensions.Options.Options.Create(options), _clock);
    }

    private static CreatePairingRequest CreateRequest(string code = "123456")
        => new(code, RelayTestFixtures.PublicKey());

    private (RelaySessionStore Store, CreatePairingResponse Desktop, RedeemPairingResponse Companion) Paired(
        Action<RelayOptions>? configure = null)
    {
        var store = CreateStore(configure);
        var desktop = store.CreatePairing(CreateRequest()).Value!;
        var companion = store.RedeemPairing(new RedeemPairingRequest("123456", RelayTestFixtures.PublicKey())).Value!;
        return (store, desktop, companion);
    }

    [Fact]
    public void CreatePairing_ReturnsSessionTokenAndSalt()
    {
        var store = CreateStore();

        var result = store.CreatePairing(CreateRequest());

        Assert.Equal(RelayStatus.Ok, result.Status);
        var response = result.Value!;
        Assert.False(string.IsNullOrWhiteSpace(response.SessionId));
        Assert.False(string.IsNullOrWhiteSpace(response.DesktopToken));
        Assert.Equal(RelayLimits.SaltBytes, Convert.FromBase64String(response.Salt).Length);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(10), response.ExpiresAt);
    }

    [Fact]
    public void CreatePairing_IssuesHighEntropyTokens()
    {
        var store = CreateStore();

        var first = store.CreatePairing(CreateRequest("111111")).Value!;
        var second = store.CreatePairing(CreateRequest("222222")).Value!;

        Assert.NotEqual(first.DesktopToken, second.DesktopToken);
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.NotEqual(first.Salt, second.Salt);
        // 32 raw bytes base64url-encoded without padding.
        Assert.Equal(43, first.DesktopToken.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("12 456")]
    [InlineData("١٢٣٤٥٦")]
    public void CreatePairing_RejectsMalformedCode(string? code)
    {
        var store = CreateStore();

        var result = store.CreatePairing(new CreatePairingRequest(code, RelayTestFixtures.PublicKey()));

        Assert.Equal(RelayStatus.Invalid, result.Status);
        Assert.Equal("invalid_pairing_code", result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")]
    public void CreatePairing_RejectsMalformedPublicKey(string? key)
    {
        var store = CreateStore();

        var result = store.CreatePairing(new CreatePairingRequest("123456", key));

        Assert.Equal(RelayStatus.Invalid, result.Status);
        Assert.Equal("invalid_public_key", result.Reason);
    }

    [Fact]
    public void CreatePairing_RejectsOversizedPublicKey()
    {
        var store = CreateStore();
        var oversized = Convert.ToBase64String(new byte[RelayLimits.MaxPublicKeyBytes + 1]);

        var result = store.CreatePairing(new CreatePairingRequest("123456", oversized));

        Assert.Equal(RelayStatus.Invalid, result.Status);
    }

    [Fact]
    public void CreatePairing_RejectsDuplicateLiveCode()
    {
        var store = CreateStore();
        store.CreatePairing(CreateRequest());

        var result = store.CreatePairing(CreateRequest());

        Assert.Equal(RelayStatus.CodeConflict, result.Status);
    }

    [Fact]
    public void CreatePairing_AllowsCodeReuseAfterExpiry()
    {
        var store = CreateStore();
        store.CreatePairing(CreateRequest());

        _clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(RelayStatus.Ok, store.CreatePairing(CreateRequest()).Status);
    }

    [Fact]
    public void CreatePairing_EnforcesSessionCap()
    {
        var store = CreateStore(o => o.MaxSessions = 2);
        store.CreatePairing(CreateRequest("111111"));
        store.CreatePairing(CreateRequest("222222"));

        var result = store.CreatePairing(CreateRequest("333333"));

        Assert.Equal(RelayStatus.CapacityExceeded, result.Status);
    }

    [Fact]
    public void RedeemPairing_ExchangesDesktopKeyAndSharesSalt()
    {
        var store = CreateStore();
        var desktop = store.CreatePairing(CreateRequest()).Value!;

        var result = store.RedeemPairing(new RedeemPairingRequest("123456", RelayTestFixtures.PublicKey()));

        Assert.Equal(RelayStatus.Ok, result.Status);
        var companion = result.Value!;
        Assert.Equal(desktop.SessionId, companion.SessionId);
        Assert.Equal(desktop.Salt, companion.Salt);
        Assert.NotEqual(desktop.DesktopToken, companion.CompanionToken);
        Assert.False(string.IsNullOrWhiteSpace(companion.DesktopPublicKey));
    }

    [Fact]
    public void RedeemPairing_IsSingleUse()
    {
        var store = CreateStore();
        store.CreatePairing(CreateRequest());
        Assert.Equal(RelayStatus.Ok, store.RedeemPairing(new RedeemPairingRequest("123456", RelayTestFixtures.PublicKey())).Status);

        var second = store.RedeemPairing(new RedeemPairingRequest("123456", RelayTestFixtures.PublicKey()));

        Assert.Equal(RelayStatus.NotFound, second.Status);
    }

    [Fact]
    public void RedeemPairing_FailsAfterTenMinutes()
    {
        var store = CreateStore();
        store.CreatePairing(CreateRequest());

        _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Assert.Equal(RelayStatus.NotFound, store.RedeemPairing(new RedeemPairingRequest("123456", RelayTestFixtures.PublicKey())).Status);
    }

    [Fact]
    public void DesktopPolling_DoesNotExtendUnredeemedPairingLifetime()
    {
        var store = CreateStore();
        var desktop = store.CreatePairing(CreateRequest()).Value!;

        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(RelayStatus.Ok, store.Poll(
            desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, 0).Status);

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(RelayStatus.Unauthorized, store.Poll(
            desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, 0).Status);
    }

    [Fact]
    public void RedeemPairing_SucceedsJustBeforeExpiry()
    {
        var store = CreateStore();
        store.CreatePairing(CreateRequest());

        _clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));

        Assert.Equal(RelayStatus.Ok, store.RedeemPairing(new RedeemPairingRequest("123456", RelayTestFixtures.PublicKey())).Status);
    }

    [Fact]
    public void RedeemPairing_UnknownCodeIsNotFound()
    {
        var store = CreateStore();

        Assert.Equal(RelayStatus.NotFound, store.RedeemPairing(new RedeemPairingRequest("999999", RelayTestFixtures.PublicKey())).Status);
    }

    [Fact]
    public void RedeemPairing_ExtendsSessionBeyondPairingWindow()
    {
        var (_, _, companion) = Paired();

        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromHours(4), companion.ExpiresAt);
    }

    [Fact]
    public void Publish_ThenPoll_DeliversToPeerOnly()
    {
        var (store, desktop, companion) = Paired();
        var envelope = RelayTestFixtures.Envelope(1);

        Assert.Equal(RelayStatus.Ok, store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, envelope).Status);

        var companionPoll = store.Poll(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, 0).Value!;
        Assert.Single(companionPoll.Messages);
        Assert.Equal(envelope.Ciphertext, companionPoll.Messages[0].Ciphertext);

        // The sender's own queue stays empty: queues are strictly directional.
        var desktopPoll = store.Poll(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, 0).Value!;
        Assert.Empty(desktopPoll.Messages);
    }

    [Fact]
    public void Poll_ReportsPeerKeyAndConnectedState()
    {
        var store = CreateStore();
        var desktop = store.CreatePairing(CreateRequest()).Value!;

        var beforeRedeem = store.Poll(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, 0).Value!;
        Assert.False(beforeRedeem.Connected);
        Assert.Null(beforeRedeem.PeerPublicKey);

        var companionKey = RelayTestFixtures.PublicKey();
        store.RedeemPairing(new RedeemPairingRequest("123456", companionKey));

        var afterRedeem = store.Poll(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, 0).Value!;
        Assert.True(afterRedeem.Connected);
        Assert.Equal(companionKey, afterRedeem.PeerPublicKey);
    }

    [Fact]
    public void Poll_AcknowledgedMessagesAreDropped()
    {
        var (store, desktop, companion) = Paired();
        store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(1));
        store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(2));

        var first = store.Poll(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, 1).Value!;
        Assert.Single(first.Messages);
        Assert.Equal(2, first.Messages[0].Sequence);

        var second = store.Poll(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, 2).Value!;
        Assert.Empty(second.Messages);
    }

    [Fact]
    public void Poll_IsRepeatableUntilAcknowledged()
    {
        var (store, desktop, companion) = Paired();
        store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(1));

        var first = store.Poll(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, 0).Value!;
        var again = store.Poll(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, 0).Value!;

        Assert.Single(first.Messages);
        Assert.Single(again.Messages);
        Assert.Equal(first.Messages[0].Sequence, again.Messages[0].Sequence);
    }

    [Fact]
    public void Poll_CapsBatchSize()
    {
        var (store, desktop, companion) = Paired(o => o.MaxQueuedMessagesPerDirection = 500);
        for (var i = 1; i <= RelayLimits.MaxPollBatchSize + 10; i++)
        {
            store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(i));
        }

        var batch = store.Poll(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, 0).Value!;

        Assert.Equal(RelayLimits.MaxPollBatchSize, batch.Messages.Count);
    }

    [Fact]
    public void Poll_RejectsNegativeWatermark()
    {
        var (store, desktop, _) = Paired();

        var result = store.Poll(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, -1);

        Assert.Equal(RelayStatus.Invalid, result.Status);
    }

    [Fact]
    public void Publish_RejectsNonMonotonicSequence()
    {
        var (store, desktop, _) = Paired();
        store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(5));

        Assert.Equal(RelayStatus.SequenceRejected, store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(5)).Status);
        Assert.Equal(RelayStatus.SequenceRejected, store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(4)).Status);
        Assert.Equal(RelayStatus.Ok, store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(6)).Status);
    }

    [Fact]
    public void Publish_SequencesAreTrackedPerDirection()
    {
        var (store, desktop, companion) = Paired();
        store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(9));

        // The companion's counter is independent, so a low sequence is still valid for it.
        Assert.Equal(RelayStatus.Ok, store.Publish(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, RelayTestFixtures.Envelope(1)).Status);
    }

    [Fact]
    public void Publish_EnforcesQueueCap()
    {
        var (store, desktop, _) = Paired(o => o.MaxQueuedMessagesPerDirection = 3);
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal(RelayStatus.Ok, store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(i)).Status);
        }

        var overflow = store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(4));

        Assert.Equal(RelayStatus.QueueFull, overflow.Status);
        Assert.Equal("queue_full", overflow.Reason);
    }

    [Fact]
    public void Publish_QueueRecoversAfterPeerDrains()
    {
        var (store, desktop, companion) = Paired(o => o.MaxQueuedMessagesPerDirection = 2);
        store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(1));
        store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(2));
        Assert.Equal(RelayStatus.QueueFull, store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(3)).Status);

        store.Poll(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, 2);

        Assert.Equal(RelayStatus.Ok, store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(3)).Status);
    }

    [Fact]
    public void Publish_RejectsForeignAndSwappedTokens()
    {
        var (store, desktop, companion) = Paired();
        var envelope = RelayTestFixtures.Envelope(1);

        // Companion token cannot act as the desktop, and vice versa.
        Assert.Equal(RelayStatus.Unauthorized, store.Publish(desktop.SessionId, RelayParticipant.Desktop, companion.CompanionToken, envelope).Status);
        Assert.Equal(RelayStatus.Unauthorized, store.Publish(desktop.SessionId, RelayParticipant.Companion, desktop.DesktopToken, envelope).Status);
    }

    [Fact]
    public void Publish_RejectsMissingOrUnknownCredentials()
    {
        var (store, desktop, _) = Paired();
        var envelope = RelayTestFixtures.Envelope(1);

        Assert.Equal(RelayStatus.Unauthorized, store.Publish(desktop.SessionId, RelayParticipant.Desktop, null, envelope).Status);
        Assert.Equal(RelayStatus.Unauthorized, store.Publish(desktop.SessionId, RelayParticipant.Desktop, "wrong-token", envelope).Status);
        Assert.Equal(RelayStatus.Unauthorized, store.Publish("no-such-session", RelayParticipant.Desktop, desktop.DesktopToken, envelope).Status);
    }

    [Fact]
    public void UnknownSessionAndBadTokenAreIndistinguishable()
    {
        var (store, desktop, _) = Paired();

        var badToken = store.Poll(desktop.SessionId, RelayParticipant.Desktop, "wrong", 0);
        var badSession = store.Poll("nope", RelayParticipant.Desktop, desktop.DesktopToken, 0);

        Assert.Equal(badToken.Status, badSession.Status);
        Assert.Equal(badToken.Reason, badSession.Reason);
    }

    [Fact]
    public void CompanionEndpointsClosedBeforeRedemption()
    {
        var store = CreateStore();
        var desktop = store.CreatePairing(CreateRequest()).Value!;

        // No companion token exists yet, so no credential can satisfy the companion role.
        var result = store.Poll(desktop.SessionId, RelayParticipant.Companion, desktop.DesktopToken, 0);

        Assert.Equal(RelayStatus.Unauthorized, result.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Publish_RejectsUnsupportedVersion(int version)
    {
        var (store, desktop, _) = Paired();
        var envelope = new RelayEnvelope(version, 1, RelayTestFixtures.Nonce(), RelayTestFixtures.Ciphertext(), "roster.snapshot");

        var result = store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, envelope);

        Assert.Equal(RelayStatus.Invalid, result.Status);
        Assert.Equal("unsupported_version", result.Reason);
    }

    [Fact]
    public void Publish_ValidatesEnvelopeBeforeAuthenticating()
    {
        var (store, desktop, _) = Paired();
        var malformed = new RelayEnvelope(RelayLimits.EnvelopeVersion, 0, RelayTestFixtures.Nonce(), RelayTestFixtures.Ciphertext(), "t");

        var result = store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, malformed);

        Assert.Equal(RelayStatus.Invalid, result.Status);
        Assert.Equal("invalid_sequence", result.Reason);
    }

    [Fact]
    public void SessionExpiresAfterIdleTimeout()
    {
        var (store, desktop, _) = Paired(o => o.SessionIdleTimeout = TimeSpan.FromMinutes(5));

        _clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(RelayStatus.Unauthorized, store.Poll(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, 0).Status);
        Assert.Equal(0, store.ActiveSessionCount);
    }

    [Fact]
    public void ActivityRefreshesIdleTimeout()
    {
        var (store, desktop, _) = Paired(o => o.SessionIdleTimeout = TimeSpan.FromMinutes(5));

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(RelayStatus.Ok, store.Poll(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, 0).Status);

        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(RelayStatus.Ok, store.Poll(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, 0).Status);
    }

    [Fact]
    public void PurgeExpiredSessions_FreesCapacity()
    {
        var store = CreateStore(o =>
        {
            o.MaxSessions = 1;
            o.SessionIdleTimeout = TimeSpan.FromMinutes(2);
        });
        store.CreatePairing(CreateRequest("111111"));

        _clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(1, store.PurgeExpiredSessions());
        Assert.Equal(RelayStatus.Ok, store.CreatePairing(CreateRequest("222222")).Status);
    }

    [Fact]
    public void Options_AreClampedToSafeBounds()
    {
        var store = CreateStore(o =>
        {
            o.PairingCodeLifetime = TimeSpan.FromDays(30);
            o.MaxSessions = -5;
            o.MaxQueuedMessagesPerDirection = 0;
        });

        Assert.Equal(TimeSpan.FromMinutes(10), store.Options.PairingCodeLifetime);
        Assert.Equal(1, store.Options.MaxSessions);
        Assert.Equal(1, store.Options.MaxQueuedMessagesPerDirection);
    }

    [Fact]
    public void ConcurrentRedemption_YieldsExactlyOneWinner()
    {
        var store = CreateStore();
        store.CreatePairing(CreateRequest());

        var results = new RelayStatus[16];
        Parallel.For(0, results.Length, i =>
        {
            results[i] = store.RedeemPairing(new RedeemPairingRequest("123456", RelayTestFixtures.PublicKey())).Status;
        });

        Assert.Equal(1, results.Count(s => s == RelayStatus.Ok));
        Assert.Equal(results.Length - 1, results.Count(s => s == RelayStatus.NotFound));
    }

    [Fact]
    public void ConcurrentPublish_KeepsSequencesStrictlyIncreasing()
    {
        var (store, desktop, companion) = Paired(o => o.MaxQueuedMessagesPerDirection = 1_000);

        Parallel.For(1, 200, i =>
            store.Publish(desktop.SessionId, RelayParticipant.Desktop, desktop.DesktopToken, RelayTestFixtures.Envelope(i)));

        var delivered = new List<long>();
        long watermark = 0;
        while (true)
        {
            var batch = store.Poll(companion.SessionId, RelayParticipant.Companion, companion.CompanionToken, watermark).Value!;
            if (batch.Messages.Count == 0)
            {
                break;
            }

            foreach (var message in batch.Messages)
            {
                delivered.Add(message.Sequence);
            }

            watermark = batch.Messages[^1].Sequence;
        }

        Assert.NotEmpty(delivered);
        Assert.Equal(delivered.OrderBy(x => x).ToList(), delivered);
        Assert.Equal(delivered.Distinct().Count(), delivered.Count);
    }
}
