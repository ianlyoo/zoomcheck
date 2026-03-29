namespace ZoomCheck.Backend.Options;

public sealed class ZoomRecoveryOptions
{
    public const string SectionName = "ZoomRecovery";

    public bool Enabled { get; set; } = true;

    public int StartupDelaySeconds { get; set; } = 8;

    public int PeriodicScanIntervalSeconds { get; set; }

    public int UsersPageSize { get; set; } = 100;

    public int MeetingsPageSize { get; set; } = 300;

    public int ParticipantsPageSize { get; set; } = 300;

    public bool EnableAccountWideUserDiscovery { get; set; } = false;

    public string[]? HostUserIds { get; set; }

    public bool IncludeFallbackMeUser { get; set; } = true;
}
