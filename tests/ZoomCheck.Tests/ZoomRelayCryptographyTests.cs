using System.Security.Cryptography;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Tests;

public sealed class ZoomRelayCryptographyTests
{
    [Fact]
    public void BrowserCompatibleKeyDerivationAndEnvelope_RoundTripsBothDirections()
    {
        using var desktop = ZoomRelayCryptography.CreateKeyPair();
        using var companion = ZoomRelayCryptography.CreateKeyPair();
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string sessionId = "relay-session-test";

        using var desktopKeys = ZoomRelayCryptography.DeriveKeys(
            desktop, ZoomRelayCryptography.ExportPublicKey(companion), salt, sessionId);
        using var companionKeys = ZoomRelayCryptography.DeriveKeys(
            companion, ZoomRelayCryptography.ExportPublicKey(desktop), salt, sessionId);

        Assert.Equal(desktopKeys.CompanionToDesktop, companionKeys.CompanionToDesktop);
        Assert.Equal(desktopKeys.DesktopToCompanion, companionKeys.DesktopToCompanion);
        Assert.NotEqual(desktopKeys.CompanionToDesktop, desktopKeys.DesktopToCompanion);

        var snapshot = new TestPayload("유영인", 3);
        var envelope = ZoomRelayCryptography.Encrypt(
            snapshot, companionKeys.CompanionToDesktop, sessionId,
            RelayDirection.CompanionToDesktop, 1, "participant-snapshot");
        var decrypted = ZoomRelayCryptography.Decrypt<TestPayload>(
            envelope, desktopKeys.CompanionToDesktop, sessionId, RelayDirection.CompanionToDesktop);

        Assert.Equal(snapshot, decrypted);
        Assert.DoesNotContain("유영인", envelope.Ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public void Envelope_TamperWrongDirectionAndReplayMetadataAreRejected()
    {
        using var desktop = ZoomRelayCryptography.CreateKeyPair();
        using var companion = ZoomRelayCryptography.CreateKeyPair();
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string sessionId = "session";
        using var keys = ZoomRelayCryptography.DeriveKeys(
            desktop, ZoomRelayCryptography.ExportPublicKey(companion), salt, sessionId);
        var envelope = ZoomRelayCryptography.Encrypt(
            new TestPayload("이순신", 1), keys.DesktopToCompanion, sessionId,
            RelayDirection.DesktopToCompanion, 7, "sync-request");

        Assert.Throws<AuthenticationTagMismatchException>(() => ZoomRelayCryptography.Decrypt<TestPayload>(
            envelope with { Sequence = 8 }, keys.DesktopToCompanion, sessionId, RelayDirection.DesktopToCompanion));
        Assert.Throws<AuthenticationTagMismatchException>(() => ZoomRelayCryptography.Decrypt<TestPayload>(
            envelope, keys.DesktopToCompanion, sessionId, RelayDirection.CompanionToDesktop));
        Assert.Throws<AuthenticationTagMismatchException>(() => ZoomRelayCryptography.Decrypt<TestPayload>(
            envelope with { MessageType = "heartbeat" }, keys.DesktopToCompanion, sessionId, RelayDirection.DesktopToCompanion));
    }

    private sealed record TestPayload(string Name, int Count);
}
