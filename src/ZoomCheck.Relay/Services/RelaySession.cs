using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Relay.Services;

/// <summary>
/// Mutable in-memory session state. Instances are only touched while holding the store's lock,
/// so no per-session synchronization is needed.
/// </summary>
internal sealed class RelaySession
{
    public required string SessionId { get; init; }

    /// <summary>Hash-only rendezvous key used to evict the code reservation. Never logged.</summary>
    public required string PairingCodeHash { get; init; }

    public required byte[] DesktopTokenHash { get; init; }

    public byte[]? CompanionTokenHash { get; set; }

    public required string DesktopPublicKey { get; init; }

    public string? CompanionPublicKey { get; set; }

    public required string Salt { get; init; }

    /// <summary>Null once the code has been redeemed; a code is strictly single-use.</summary>
    public DateTimeOffset? PairingCodeExpiresAt { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    public bool Connected => CompanionTokenHash is not null;

    /// <summary>Envelopes awaiting the desktop, i.e. written by the companion.</summary>
    public Queue<RelayEnvelope> ToDesktop { get; } = new();

    /// <summary>Envelopes awaiting the companion, i.e. written by the desktop.</summary>
    public Queue<RelayEnvelope> ToCompanion { get; } = new();

    public long LastDesktopSequence { get; set; }

    public long LastCompanionSequence { get; set; }

    public Queue<RelayEnvelope> QueueFor(RelayParticipant reader)
        => reader == RelayParticipant.Desktop ? ToDesktop : ToCompanion;

    public byte[]? TokenHashFor(RelayParticipant participant)
        => participant == RelayParticipant.Desktop ? DesktopTokenHash : CompanionTokenHash;

    public string? PublicKeyFor(RelayParticipant participant)
        => participant == RelayParticipant.Desktop ? DesktopPublicKey : CompanionPublicKey;

    public long LastSequenceFor(RelayParticipant sender)
        => sender == RelayParticipant.Desktop ? LastDesktopSequence : LastCompanionSequence;

    public void SetLastSequence(RelayParticipant sender, long sequence)
    {
        if (sender == RelayParticipant.Desktop)
        {
            LastDesktopSequence = sequence;
        }
        else
        {
            LastCompanionSequence = sequence;
        }
    }
}
