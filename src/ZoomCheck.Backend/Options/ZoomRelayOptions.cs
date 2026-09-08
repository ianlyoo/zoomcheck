namespace ZoomCheck.Backend.Options;

public sealed class ZoomRelayOptions
{
    public const string SectionName = "ZoomRelay";

    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = string.Empty;

    public int PollIntervalMilliseconds { get; set; } = 1500;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int OfflineSeconds { get; set; } = 20;
}
