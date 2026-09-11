namespace ZoomCheck.Relay.Services;

/// <summary>Which side of a paired session a request is acting as.</summary>
public enum RelayParticipant
{
    Desktop,
    Companion,
}

public static class RelayParticipantExtensions
{
    public static RelayParticipant Peer(this RelayParticipant participant)
        => participant == RelayParticipant.Desktop ? RelayParticipant.Companion : RelayParticipant.Desktop;
}
