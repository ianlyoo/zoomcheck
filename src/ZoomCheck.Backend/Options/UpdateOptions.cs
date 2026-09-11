namespace ZoomCheck.Backend.Options;

/// <summary>
/// Configuration for the Windows self-update flow. Everything here is public
/// metadata: the GitHub Releases API needs no credentials, so no secret ever
/// reaches this options object.
/// </summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    public bool Enabled { get; set; } = true;

    /// <summary>owner/repo slug that publishes the signed installer.</summary>
    public string Repository { get; set; } = "ianlyoo/zoomcheck";

    public string ApiBaseUrl { get; set; } = "https://api.github.com/";

    /// <summary>The only asset this client is ever allowed to download and run.</summary>
    public string InstallerAssetName { get; set; } = "ZoomCheck-Setup-x64.exe";

    public string ChecksumAssetName { get; set; } = "SHA256SUMS.txt";

    /// <summary>
    /// Prereleases (for example -rc tags) are considered when this is true or
    /// when the running build is itself a prerelease. Defaults to true so the
    /// current release-candidate line can keep updating itself.
    /// </summary>
    public bool AllowPrerelease { get; set; } = true;

    public bool CheckOnStartup { get; set; } = true;

    public int StartupDelaySeconds { get; set; } = 10;

    /// <summary>Zero or less disables periodic rechecks.</summary>
    public int CheckIntervalHours { get; set; } = 12;

    /// <summary>Download the verified installer as soon as an update is found.</summary>
    public bool AutomaticDownload { get; set; } = true;

    public string UserAgent { get; set; } = "ZoomCheck-Updater";

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int ReleasePageSize { get; set; } = 20;

    public long MaxInstallerBytes { get; set; } = 300L * 1024 * 1024;

    public long MaxChecksumBytes { get; set; } = 512L * 1024;

    /// <summary>Inno Setup switches: progress window only, no prompts, no reboot.</summary>
    public string InstallerArguments { get; set; } = "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    /// <summary>Let the HTTP response flush and the installer start before releasing our file locks.</summary>
    public bool ShutdownAfterLaunch { get; set; } = true;

    public int ShutdownDelaySeconds { get; set; } = 3;

    /// <summary>Overrides the assembly version. Intended for support and tests.</summary>
    public string? CurrentVersionOverride { get; set; }
}
