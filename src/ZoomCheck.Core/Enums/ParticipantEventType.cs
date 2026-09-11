namespace ZoomCheck.Core.Enums;

public enum ParticipantEventType
{
    Joined = 0,
    Left = 1,

    /// <summary>
    /// An already-present connection (same stable presence key) changed its display name.
    /// </summary>
    NameChanged = 2
}
