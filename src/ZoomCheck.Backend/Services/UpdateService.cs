using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

/// <summary>
/// Checks GitHub Releases for a newer ZoomCheck build, downloads the single
/// approved installer asset plus its published SHA-256 list, and only ever
/// launches an installer whose hash matched. Network failures are recorded and
/// reported but never thrown at the host.
/// </summary>
public sealed class UpdateService
{
    // GitHub's REST payloads are snake_case (tag_name, browser_download_url, published_at).
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;
    private readonly UpdateOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UpdateService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _downloadDirectory;
    private readonly object _stateLock = new();

    private UpdateState _state = UpdateState.Initial;

    public UpdateService(
        HttpClient http,
        IOptions<UpdateOptions> options,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        ILogger<UpdateService> logger)
        : this(http, options, lifetime, timeProvider, logger, downloadDirectory: null)
    {
    }

    internal UpdateService(
        HttpClient http,
        IOptions<UpdateOptions> options,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        ILogger<UpdateService> logger,
        string? downloadDirectory)
    {
        _http = http;
        _options = options.Value;
        _lifetime = lifetime;
        _timeProvider = timeProvider;
        _logger = logger;
        _downloadDirectory = downloadDirectory ?? DefaultDownloadDirectory();

        if (Uri.TryCreate(_options.ApiBaseUrl, UriKind.Absolute, out var apiBase)
            && apiBase.Scheme == Uri.UriSchemeHttps)
        {
            _http.BaseAddress = apiBase;
        }

        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 5, 300));
        // GitHub rejects API calls without a User-Agent.
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                SanitizeProduct(_options.UserAgent),
                CurrentVersion.ToString()));
        }

        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (!_http.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
        {
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }
    }

    /// <summary>Version of the running build, from AssemblyInformationalVersion.</summary>
    public SemanticVersion CurrentVersion => ResolveCurrentVersion();

    public bool SupportedPlatform => OperatingSystem.IsWindows();

    internal string DownloadDirectory => _downloadDirectory;

    public UpdateStatusResponse GetStatus()
    {
        lock (_stateLock)
        {
            return BuildStatus(_state);
        }
    }

    /// <summary>
    /// Queries GitHub for the newest eligible release and, when configured,
    /// downloads plus verifies the installer. Never throws for network trouble.
    /// </summary>
    public async Task<UpdateStatusResponse> CheckAsync(
        bool manual,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Mutate(state => state with
            {
                Availability = UpdateAvailability.Disabled,
                Message = "Updates are disabled by configuration."
            });
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            GitHubRelease? release;
            try
            {
                release = await FetchLatestEligibleReleaseAsync(manual, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or IOException or InvalidOperationException)
            {
                // Update checks are best effort: a flaky network must not disturb attendance work.
                _logger.LogWarning(ex, "Update check could not reach GitHub Releases.");
                return Mutate(state => state with
                {
                    LastCheckedAt = now,
                    Availability = UpdateAvailability.Failed,
                    Message = "Could not reach the update service."
                });
            }

            if (release is null)
            {
                return Mutate(state => state with
                {
                    LastCheckedAt = now,
                    LatestVersion = null,
                    Availability = UpdateAvailability.UpToDate,
                    Message = "No eligible release was published."
                });
            }

            var current = CurrentVersion;
            var isNewer = release.Version.CompareTo(current) > 0;
            var status = Mutate(existing => existing with
            {
                LastCheckedAt = now,
                LatestVersion = release.Version,
                LatestIsPrerelease = release.IsPrerelease,
                ReleaseName = release.Name,
                ReleaseNotes = release.Body,
                ReleasePublishedAt = release.PublishedAt,
                Availability = isNewer ? UpdateAvailability.UpdateAvailable : UpdateAvailability.UpToDate,
                Message = isNewer
                    ? string.Create(CultureInfo.InvariantCulture, $"Version {release.Version} is available.")
                    : "ZoomCheck is up to date."
            });

            if (!isNewer)
            {
                return status;
            }

            bool alreadyStaged;
            lock (_stateLock)
            {
                alreadyStaged = _state.DownloadState == UpdateDownloadState.Verified
                    && _state.VerifiedVersion is not null
                    && _state.VerifiedVersion.CompareTo(release.Version) == 0
                    && _state.InstallerPath is not null
                    && File.Exists(_state.InstallerPath);
            }
            if (alreadyStaged)
            {
                return status;
            }

            if (!SupportedPlatform || !_options.AutomaticDownload)
            {
                return status;
            }

            // A stable build may be told about a prerelease by a manual check, but
            // it is never silently upgraded onto the prerelease channel.
            if (release.IsPrerelease && !ShouldConsiderPrereleases(manual: false))
            {
                return status;
            }

            return await DownloadAndVerifyAsync(release, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Launches the staged installer after re-verifying its hash, then asks the
    /// host to shut down so the installer can replace files in use.
    /// </summary>
    public async Task<UpdateInstallResponse> InstallAsync(CancellationToken cancellationToken)
    {
        if (!SupportedPlatform)
        {
            return new UpdateInstallResponse(false, null, false, "Automatic install is available on Windows only.");
        }

        if (!_options.Enabled)
        {
            return new UpdateInstallResponse(false, null, false, "Updates are disabled by configuration.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            UpdateState snapshot;
            lock (_stateLock)
            {
                snapshot = _state;
            }

            if (snapshot.DownloadState != UpdateDownloadState.Verified
                || snapshot.InstallerPath is null
                || snapshot.VerifiedVersion is null
                || snapshot.VerifiedSha256 is null)
            {
                return new UpdateInstallResponse(false, null, false, "No verified update is ready to install.");
            }

            if (!File.Exists(snapshot.InstallerPath))
            {
                Mutate(state => state with
                {
                    DownloadState = UpdateDownloadState.None,
                    InstallerPath = null,
                    InstallerSizeBytes = null,
                    VerifiedVersion = null,
                    VerifiedSha256 = null,
                    Message = "The downloaded installer is no longer present."
                });
                return new UpdateInstallResponse(false, null, false, "The downloaded installer is no longer present.");
            }

            // Re-hash immediately before execution: the file may have been swapped
            // on disk since it was verified.
            var actual = await ComputeSha256Async(snapshot.InstallerPath, cancellationToken);
            if (!HashEquals(actual, snapshot.VerifiedSha256))
            {
                TryDelete(snapshot.InstallerPath);
                Mutate(state => state with
                {
                    DownloadState = UpdateDownloadState.VerificationFailed,
                    InstallerPath = null,
                    InstallerSizeBytes = null,
                    VerifiedVersion = null,
                    VerifiedSha256 = null,
                    Message = "The staged installer failed re-verification and was discarded."
                });
                _logger.LogError("Staged ZoomCheck installer failed SHA-256 re-verification; it was deleted.");
                return new UpdateInstallResponse(false, null, false, "The staged installer failed verification.");
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = snapshot.InstallerPath,
                    Arguments = _options.InstallerArguments,
                    UseShellExecute = false,
                    WorkingDirectory = _downloadDirectory
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not launch the ZoomCheck installer.");
                return new UpdateInstallResponse(false, snapshot.VerifiedVersion.ToString(), false,
                    "The installer could not be started.");
            }

            var shutdownRequested = _options.ShutdownAfterLaunch;
            if (shutdownRequested)
            {
                RequestDeferredShutdown();
            }

            Mutate(state => state with
            {
                Message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Installing version {state.VerifiedVersion}.")
            });

            return new UpdateInstallResponse(
                true,
                snapshot.VerifiedVersion.ToString(),
                shutdownRequested,
                shutdownRequested
                    ? "The installer is running and ZoomCheck will close shortly."
                    : "The installer is running.");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<UpdateStatusResponse> DownloadAndVerifyAsync(
        GitHubRelease release,
        CancellationToken cancellationToken)
    {
        var installerAsset = FindAsset(release, _options.InstallerAssetName);
        if (installerAsset is null)
        {
            return Mutate(state => state with
            {
                DownloadState = UpdateDownloadState.Failed,
                Message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Release {release.Version} has no {_options.InstallerAssetName} asset.")
            });
        }

        var checksumAsset = FindAsset(release, _options.ChecksumAssetName);
        if (checksumAsset is null)
        {
            return Mutate(state => state with
            {
                DownloadState = UpdateDownloadState.Failed,
                Message = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Release {release.Version} publishes no {_options.ChecksumAssetName}, so it was not downloaded.")
            });
        }

        Mutate(state => state with
        {
            DownloadState = UpdateDownloadState.Downloading,
            Message = string.Create(CultureInfo.InvariantCulture, $"Downloading version {release.Version}.")
        });

        string installerPath;
        string expectedHash;
        try
        {
            Directory.CreateDirectory(_downloadDirectory);

            var checksumPath = Path.Combine(_downloadDirectory, _options.ChecksumAssetName);
            await DownloadAssetAsync(checksumAsset, checksumPath, _options.MaxChecksumBytes, cancellationToken);
            var checksumText = await File.ReadAllTextAsync(checksumPath, cancellationToken);
            if (!TryReadExpectedHash(checksumText, _options.InstallerAssetName, out var parsedHash))
            {
                return Mutate(state => state with
                {
                    DownloadState = UpdateDownloadState.Failed,
                    Message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{_options.ChecksumAssetName} has no entry for {_options.InstallerAssetName}.")
                });
            }

            expectedHash = parsedHash;
            installerPath = Path.Combine(_downloadDirectory, _options.InstallerAssetName);
            await DownloadAssetAsync(installerAsset, installerPath, _options.MaxInstallerBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                   or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Downloading the ZoomCheck update failed.");
            return Mutate(state => state with
            {
                DownloadState = UpdateDownloadState.Failed,
                InstallerPath = null,
                InstallerSizeBytes = null,
                VerifiedVersion = null,
                VerifiedSha256 = null,
                Message = "The update download did not finish."
            });
        }

        var actualHash = await ComputeSha256Async(installerPath, cancellationToken);
        if (!HashEquals(actualHash, expectedHash))
        {
            // An unverified installer is never kept, let alone executed.
            TryDelete(installerPath);
            _logger.LogError(
                "Downloaded {AssetName} for version {Version} failed SHA-256 verification and was deleted.",
                _options.InstallerAssetName,
                release.Version.ToString());
            return Mutate(state => state with
            {
                DownloadState = UpdateDownloadState.VerificationFailed,
                InstallerPath = null,
                InstallerSizeBytes = null,
                VerifiedVersion = null,
                VerifiedSha256 = null,
                Message = "The downloaded installer failed SHA-256 verification and was discarded."
            });
        }

        var size = new FileInfo(installerPath).Length;
        _logger.LogInformation(
            "Verified ZoomCheck {Version} installer ({SizeBytes} bytes) and staged it for install.",
            release.Version.ToString(),
            size);

        return Mutate(state => state with
        {
            DownloadState = UpdateDownloadState.Verified,
            InstallerPath = installerPath,
            InstallerSizeBytes = size,
            VerifiedVersion = release.Version,
            VerifiedSha256 = actualHash,
            Message = string.Create(
                CultureInfo.InvariantCulture,
                $"Version {release.Version} is verified and ready to install.")
        });
    }

    internal async Task<GitHubRelease?> FetchLatestEligibleReleaseAsync(
        bool manual,
        CancellationToken cancellationToken)
    {
        var repository = NormalizeRepository(_options.Repository);
        if (repository is null)
        {
            throw new InvalidOperationException("Update repository is not configured as owner/repo.");
        }

        var pageSize = Math.Clamp(_options.ReleasePageSize, 1, 100);
        // The releases list is used instead of /releases/latest because the latter
        // hides prereleases, which the current rc line still needs.
        using var response = await _http.GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"repos/{repository}/releases?per_page={pageSize}"),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<List<GitHubReleasePayload>>(
            stream, JsonOptions, cancellationToken);
        if (payload is null || payload.Count == 0)
        {
            return null;
        }

        var includePrerelease = ShouldConsiderPrereleases(manual);
        GitHubRelease? best = null;
        foreach (var candidate in payload)
        {
            if (candidate.Draft)
            {
                continue;
            }

            if (!SemanticVersion.TryParse(candidate.TagName ?? candidate.Name, out var version))
            {
                continue;
            }

            var prerelease = candidate.Prerelease || version.IsPrerelease;
            if (prerelease && !includePrerelease)
            {
                continue;
            }

            var release = new GitHubRelease(
                version,
                prerelease,
                candidate.Name,
                candidate.Body,
                candidate.PublishedAt,
                (candidate.Assets ?? new List<GitHubAssetPayload>())
                    .Where(asset => !string.IsNullOrWhiteSpace(asset.Name)
                        && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                    .Select(asset => new GitHubAsset(asset.Name!, asset.BrowserDownloadUrl!, asset.Size))
                    .ToArray());

            if (best is null || release.Version.CompareTo(best.Version) > 0)
            {
                best = release;
            }
        }

        return best;
    }

    /// <summary>
    /// A build that is itself a prerelease stays on the prerelease channel. A
    /// stable build ignores prereleases during automatic checks, and reports them
    /// on a manual check only when configuration opts in.
    /// </summary>
    internal bool ShouldConsiderPrereleases(bool manual)
        => CurrentVersion.IsPrerelease || (manual && _options.AllowPrerelease);

    internal static bool TryReadExpectedHash(string checksumText, string assetName, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(checksumText))
        {
            return false;
        }

        foreach (var rawLine in checksumText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOfAny(new[] { ' ', '\t' });
            if (separator <= 0)
            {
                continue;
            }

            var candidateHash = line[..separator].Trim();
            // sha256sum marks binary mode with a leading '*' on the file name.
            var candidateName = line[(separator + 1)..].Trim().TrimStart('*', ' ', '\t');
            if (candidateName.Length == 0)
            {
                continue;
            }

            candidateName = candidateName.Replace('\\', '/');
            var lastSlash = candidateName.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                candidateName = candidateName[(lastSlash + 1)..];
            }

            if (!string.Equals(candidateName, assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsSha256Hex(candidateHash))
            {
                continue;
            }

            hash = candidateHash.ToLowerInvariant();
            return true;
        }

        return false;
    }

    /// <summary>Case-insensitive, fixed-length comparison of two hex digests.</summary>
    internal static bool HashEquals(string? left, string? right)
        => IsSha256Hex(left)
            && IsSha256Hex(right)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();
        var digest = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            var isHex = character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    private GitHubAsset? FindAsset(GitHubRelease release, string assetName)
        => release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));

    private async Task DownloadAssetAsync(
        GitHubAsset asset,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Release assets must be served over HTTPS.");
        }

        if (asset.Size > 0 && asset.Size > maxBytes)
        {
            throw new InvalidOperationException("Release asset exceeds the configured size limit.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/octet-stream");
        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw new InvalidOperationException("Release asset exceeds the configured size limit.");
        }

        var temporaryPath = destinationPath + ".part";
        TryDelete(temporaryPath);

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(
            temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new InvalidOperationException("Release asset exceeds the configured size limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private void RequestDeferredShutdown()
    {
        var delay = TimeSpan.FromSeconds(Math.Clamp(_options.ShutdownDelaySeconds, 0, 120));
        // Deferred so the HTTP response reaches the dashboard and the installer
        // gets a moment to start before this process releases its files.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Update shutdown delay was interrupted.");
            }

            _logger.LogInformation("Stopping ZoomCheck so the update installer can replace files in use.");
            _lifetime.StopApplication();
        });
    }

    private SemanticVersion ResolveCurrentVersion()
    {
        if (SemanticVersion.TryParse(_options.CurrentVersionOverride, out var configured))
        {
            return configured;
        }

        var assembly = typeof(UpdateService).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemanticVersion.TryParse(informational, out var informationalVersion))
        {
            return informationalVersion;
        }

        if (SemanticVersion.TryParse(assembly.GetName().Version?.ToString(), out var assemblyVersion))
        {
            return assemblyVersion;
        }

        SemanticVersion.TryParse("0.0.0", out var fallback);
        return fallback!;
    }

    private UpdateStatusResponse BuildStatus(UpdateState state)
    {
        var availability = _options.Enabled ? state.Availability : UpdateAvailability.Disabled;
        if (_options.Enabled && !SupportedPlatform && availability == UpdateAvailability.Unknown)
        {
            availability = UpdateAvailability.Unsupported;
        }

        var ready = state.DownloadState == UpdateDownloadState.Verified
            && state.InstallerPath is not null
            && File.Exists(state.InstallerPath);

        return new UpdateStatusResponse(
            Enabled: _options.Enabled,
            SupportedPlatform: SupportedPlatform,
            CurrentVersion: CurrentVersion.ToString(),
            LatestVersion: state.LatestVersion?.ToString(),
            LatestIsPrerelease: state.LatestIsPrerelease,
            PrereleasesConsidered: ShouldConsiderPrereleases(manual: true),
            Availability: availability,
            DownloadState: state.DownloadState,
            InstallerReady: ready,
            InstallerFileName: ready ? Path.GetFileName(state.InstallerPath) : null,
            InstallerSizeBytes: ready ? state.InstallerSizeBytes : null,
            LastCheckedAt: state.LastCheckedAt,
            ReleasePublishedAt: state.ReleasePublishedAt,
            ReleaseName: state.ReleaseName,
            ReleaseNotes: state.ReleaseNotes,
            Message: state.Message);
    }

    private UpdateStatusResponse Mutate(Func<UpdateState, UpdateState> transform)
    {
        lock (_stateLock)
        {
            _state = transform(_state);
            return BuildStatus(_state);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove a staged update file.");
        }
    }

    private static string DefaultDownloadDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZoomCheck",
            "updates");

    private static string SanitizeProduct(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "ZoomCheck-Updater";
        }

        var characters = value.Trim()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-')
            .ToArray();
        return new string(characters);
    }

    private sealed record UpdateState(
        UpdateAvailability Availability,
        UpdateDownloadState DownloadState,
        SemanticVersion? LatestVersion,
        bool LatestIsPrerelease,
        string? InstallerPath,
        long? InstallerSizeBytes,
        SemanticVersion? VerifiedVersion,
        string? VerifiedSha256,
        DateTimeOffset? LastCheckedAt,
        DateTimeOffset? ReleasePublishedAt,
        string? ReleaseName,
        string? ReleaseNotes,
        string? Message)
    {
        public static readonly UpdateState Initial = new(
            UpdateAvailability.Unknown,
            UpdateDownloadState.None,
            null,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    internal sealed record GitHubRelease(
        SemanticVersion Version,
        bool IsPrerelease,
        string? Name,
        string? Body,
        DateTimeOffset? PublishedAt,
        IReadOnlyList<GitHubAsset> Assets);

    internal sealed record GitHubAsset(string Name, string DownloadUrl, long Size);

    private sealed class GitHubReleasePayload
    {
        public string? TagName { get; set; }

        public string? Name { get; set; }

        public string? Body { get; set; }

        public bool Draft { get; set; }

        public bool Prerelease { get; set; }

        public DateTimeOffset? PublishedAt { get; set; }

        public List<GitHubAssetPayload>? Assets { get; set; }
    }

    private sealed class GitHubAssetPayload
    {
        public string? Name { get; set; }

        public string? BrowserDownloadUrl { get; set; }

        public long Size { get; set; }
    }

    private static string? NormalizeRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        var parts = repository.Trim().Trim('/').Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        return string.Concat(Uri.EscapeDataString(parts[0]), "/", Uri.EscapeDataString(parts[1]));
    }
}
