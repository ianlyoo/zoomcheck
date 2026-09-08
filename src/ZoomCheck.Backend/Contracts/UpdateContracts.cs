namespace ZoomCheck.Backend.Contracts;

public enum UpdateAvailability
{
    Unknown = 0,
    UpToDate = 1,
    UpdateAvailable = 2,
    Disabled = 3,
    Unsupported = 4,
    Failed = 5
}

public enum UpdateDownloadState
{
    None = 0,
    Downloading = 1,
    Verified = 2,
    VerificationFailed = 3,
    Failed = 4
}

/// <summary>
/// Snapshot of the updater for the local dashboard. Deliberately carries no
/// download URL or filesystem path beyond the installer file name so the API
/// surface stays free of anything worth leaking.
/// </summary>
public sealed record UpdateStatusResponse(
    bool Enabled,
    bool SupportedPlatform,
    string CurrentVersion,
    string? LatestVersion,
    bool LatestIsPrerelease,
    bool PrereleasesConsidered,
    UpdateAvailability Availability,
    UpdateDownloadState DownloadState,
    bool InstallerReady,
    string? InstallerFileName,
    long? InstallerSizeBytes,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? ReleasePublishedAt,
    string? ReleaseName,
    string? ReleaseNotes,
    string? Message);

public sealed record UpdateInstallResponse(
    bool Started,
    string? Version,
    bool ShutdownRequested,
    string? Message);
