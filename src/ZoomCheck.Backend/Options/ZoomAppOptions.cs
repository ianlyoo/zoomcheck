namespace ZoomCheck.Backend.Options;

public sealed class ZoomAppOptions
{
    public const string SectionName = "ZoomApp";

    /// <summary>HTTPS Home URL configured for the Zoom App companion.</summary>
    public string HomeUrl { get; set; } = string.Empty;

    /// <summary>Origins allowed to call the local bridge. HomeUrl's origin is added automatically.</summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    public int PairingCodeLifetimeSeconds { get; set; } = 600;

    public int SessionOfflineSeconds { get; set; } = 20;
}
