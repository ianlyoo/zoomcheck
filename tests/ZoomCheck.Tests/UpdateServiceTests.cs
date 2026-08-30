using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Controllers;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("v0.6.42", 0, 6, 42, null)]
    [InlineData("0.7.0-rc.1", 0, 7, 0, "rc.1")]
    [InlineData("0.6.42+abc1234", 0, 6, 42, null)]
    [InlineData("1.2.3.4", 1, 2, 3, null)]
    public void Parses_SupportedFormats(string input, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(SemanticVersion.TryParse(input, out var version));
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.x.0")]
    [InlineData("1.2.3-")]
    public void Rejects_UnparseableVersions(string input)
        => Assert.False(SemanticVersion.TryParse(input, out _));

    [Fact]
    public void Stable_Outranks_PrereleaseOfSameCore()
    {
        Assert.True(SemanticVersion.TryParse("0.7.0", out var stable));
        Assert.True(SemanticVersion.TryParse("0.7.0-rc.2", out var candidate));
        Assert.True(stable!.CompareTo(candidate!) > 0);
    }

    [Fact]
    public void Prerelease_Identifiers_CompareNumerically()
    {
        Assert.True(SemanticVersion.TryParse("0.7.0-rc.2", out var lower));
        Assert.True(SemanticVersion.TryParse("0.7.0-rc.10", out var higher));
        Assert.True(higher!.CompareTo(lower!) > 0);
    }

    [Fact]
    public void BuildMetadata_DoesNotAffectPrecedence()
    {
        Assert.True(SemanticVersion.TryParse("0.6.42+aaa", out var left));
        Assert.True(SemanticVersion.TryParse("0.6.42+bbb", out var right));
        Assert.Equal(0, left!.CompareTo(right!));
    }
}

public sealed class UpdateChecksumParsingTests
{
    [Fact]
    public void Reads_LowercaseEntry()
    {
        var text = "3b1f2c" + new string('a', 58) + "  ZoomCheck-Setup-x64.exe\n";

        Assert.True(UpdateService.TryReadExpectedHash(text, "ZoomCheck-Setup-x64.exe", out var hash));
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void Reads_UppercaseBinaryModeEntryWithPathPrefix()
    {
        var digest = new string('A', 64);
        var text = "# checksums\n" + digest + " *dist/installer/ZoomCheck-Setup-x64.exe\r\n";

        Assert.True(UpdateService.TryReadExpectedHash(text, "ZoomCheck-Setup-x64.exe", out var hash));
        Assert.Equal(digest.ToLowerInvariant(), hash);
    }

    [Fact]
    public void Ignores_OtherAssets()
    {
        var text = new string('b', 64) + "  ZoomCheck-portable-win-x64.zip\n";

        Assert.False(UpdateService.TryReadExpectedHash(text, "ZoomCheck-Setup-x64.exe", out _));
    }

    [Fact]
    public void Ignores_MalformedHash()
    {
        var text = "notahash  ZoomCheck-Setup-x64.exe\n";

        Assert.False(UpdateService.TryReadExpectedHash(text, "ZoomCheck-Setup-x64.exe", out _));
    }

    [Fact]
    public void HashEquals_IsCaseInsensitiveAndLengthChecked()
    {
        var lower = new string('a', 64);
        var upper = new string('A', 64);

        Assert.True(UpdateService.HashEquals(lower, upper));
        Assert.False(UpdateService.HashEquals(lower, new string('a', 63)));
        Assert.False(UpdateService.HashEquals(null, lower));
    }

    [Fact]
    public async Task ComputeSha256Async_MatchesKnownDigest()
    {
        var directory = Directory.CreateTempSubdirectory("zoomcheck-update-hash");
        try
        {
            var path = Path.Combine(directory.FullName, "payload.bin");
            await File.WriteAllTextAsync(path, "abc");

            var hash = await UpdateService.ComputeSha256Async(path, CancellationToken.None);

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

public sealed class UpdateServiceStatusTests
{
    [Fact]
    public void ControllerRoute_MatchesDashboardContract()
    {
        var route = Assert.Single(typeof(UpdatesController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>());

        Assert.Equal("api/update", route.Template);
    }

    [Fact]
    public void Status_ReportsConfiguredCurrentVersion()
    {
        using var harness = new UpdateServiceHarness(new UpdateOptions
        {
            CurrentVersionOverride = "0.6.42"
        });

        var status = harness.Service.GetStatus();

        Assert.Equal("0.6.42", status.CurrentVersion);
        Assert.Equal(UpdateDownloadState.None, status.DownloadState);
        Assert.False(status.InstallerReady);
        Assert.Null(status.InstallerFileName);
    }

    [Fact]
    public async Task DisabledUpdates_ShortCircuitCheck()
    {
        using var harness = new UpdateServiceHarness(new UpdateOptions
        {
            Enabled = false,
            CurrentVersionOverride = "0.6.42"
        });

        var status = await harness.Service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateAvailability.Disabled, status.Availability);
    }

    [Fact]
    public async Task UnreachableGitHub_IsNonFatal()
    {
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.6.42" },
            _ => throw new HttpRequestException("offline"));

        var status = await harness.Service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(UpdateAvailability.Failed, status.Availability);
        Assert.NotNull(status.LastCheckedAt);
        Assert.Equal("Could not reach the update service.", status.Message);
    }

    [Fact]
    public async Task Install_WithoutVerifiedAsset_IsRefused()
    {
        using var harness = new UpdateServiceHarness(new UpdateOptions
        {
            CurrentVersionOverride = "0.6.42"
        });

        var result = await harness.Service.InstallAsync(CancellationToken.None);

        Assert.False(result.Started);
        Assert.False(result.ShutdownRequested);
    }

    [Fact]
    public void StableBuild_IgnoresPrereleasesForAutomaticChecks()
    {
        using var harness = new UpdateServiceHarness(new UpdateOptions
        {
            CurrentVersionOverride = "0.6.42",
            AllowPrerelease = true
        });

        Assert.False(harness.Service.ShouldConsiderPrereleases(manual: false));
        Assert.True(harness.Service.ShouldConsiderPrereleases(manual: true));
    }

    [Fact]
    public void StableBuild_WithPrereleasesOptedOut_NeverConsidersThem()
    {
        using var harness = new UpdateServiceHarness(new UpdateOptions
        {
            CurrentVersionOverride = "0.6.42",
            AllowPrerelease = false
        });

        Assert.False(harness.Service.ShouldConsiderPrereleases(manual: true));
    }

    [Fact]
    public void PrereleaseBuild_StaysOnPrereleaseChannel()
    {
        using var harness = new UpdateServiceHarness(new UpdateOptions
        {
            CurrentVersionOverride = "0.7.0-rc.1",
            AllowPrerelease = false
        });

        Assert.True(harness.Service.ShouldConsiderPrereleases(manual: false));
    }

    [Fact]
    public async Task Check_PicksHighestEligibleRelease()
    {
        const string payload = """
        [
          {"tag_name":"v0.6.40","name":"0.6.40","draft":false,"prerelease":false,"assets":[]},
          {"tag_name":"v0.7.0-rc.1","name":"rc","draft":false,"prerelease":true,"assets":[]},
          {"tag_name":"v0.9.0","name":"draft","draft":true,"prerelease":false,"assets":[]}
        ]
        """;
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.7.0-rc.1", AutomaticDownload = false },
            _ => JsonResponse(payload));

        var release = await harness.Service.FetchLatestEligibleReleaseAsync(manual: false, CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal("0.7.0-rc.1", release!.Version.ToString());
    }

    [Fact]
    public async Task Check_StableBuild_SkipsPrereleaseTags()
    {
        const string payload = """
        [
          {"tag_name":"v0.6.42","draft":false,"prerelease":false,"assets":[]},
          {"tag_name":"v0.8.0-rc.3","draft":false,"prerelease":true,"assets":[]}
        ]
        """;
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.6.42", AllowPrerelease = true, AutomaticDownload = false },
            _ => JsonResponse(payload));

        var automatic = await harness.Service.FetchLatestEligibleReleaseAsync(manual: false, CancellationToken.None);
        var manual = await harness.Service.FetchLatestEligibleReleaseAsync(manual: true, CancellationToken.None);

        Assert.Equal("0.6.42", automatic!.Version.ToString());
        Assert.Equal("0.8.0-rc.3", manual!.Version.ToString());
    }

    [Fact]
    public async Task Check_SendsUserAgent()
    {
        HttpRequestMessage? captured = null;
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.6.42", AutomaticDownload = false },
            request =>
            {
                captured = request;
                return JsonResponse("[]");
            });

        await harness.Service.CheckAsync(manual: true, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.NotEmpty(captured!.Headers.UserAgent);
        Assert.StartsWith("https://api.github.com/", captured.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_VerifiesMatchingChecksum_AndStagesInstaller()
    {
        var installerBytes = new byte[] { 1, 2, 3, 4, 5 };
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerBytes)).ToUpperInvariant();
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.6.42" },
            request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                ? TextResponse(digest + "  ZoomCheck-Setup-x64.exe\n")
                : BinaryResponse(installerBytes));

        var status = await harness.Service.DownloadAndVerifyAsync(
            harness.Release("0.7.0"), CancellationToken.None);

        Assert.Equal(UpdateDownloadState.Verified, status.DownloadState);
        Assert.True(status.InstallerReady);
        Assert.Equal("ZoomCheck-Setup-x64.exe", status.InstallerFileName);
        Assert.Equal(installerBytes.Length, status.InstallerSizeBytes);
        Assert.True(File.Exists(Path.Combine(harness.DownloadDirectory, "ZoomCheck-Setup-x64.exe")));
    }

    [Fact]
    public async Task Download_WithMismatchedChecksum_DeletesInstallerAndBlocksInstall()
    {
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.6.42" },
            request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                ? TextResponse(new string('c', 64) + "  ZoomCheck-Setup-x64.exe\n")
                : BinaryResponse(new byte[] { 9, 9, 9 }));

        var status = await harness.Service.DownloadAndVerifyAsync(
            harness.Release("0.7.0"), CancellationToken.None);

        Assert.Equal(UpdateDownloadState.VerificationFailed, status.DownloadState);
        Assert.False(status.InstallerReady);
        Assert.False(File.Exists(Path.Combine(harness.DownloadDirectory, "ZoomCheck-Setup-x64.exe")));

        var install = await harness.Service.InstallAsync(CancellationToken.None);
        Assert.False(install.Started);
    }

    [Fact]
    public async Task Download_WithoutChecksumAsset_IsRefused()
    {
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.6.42" },
            _ => BinaryResponse(new byte[] { 1 }));

        var status = await harness.Service.DownloadAndVerifyAsync(
            harness.Release("0.7.0", includeChecksumAsset: false), CancellationToken.None);

        Assert.Equal(UpdateDownloadState.Failed, status.DownloadState);
        Assert.False(File.Exists(Path.Combine(harness.DownloadDirectory, "ZoomCheck-Setup-x64.exe")));
    }

    [Fact]
    public async Task Download_WithoutInstallerAsset_IsRefused()
    {
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.6.42" },
            _ => TextResponse("unused"));

        var status = await harness.Service.DownloadAndVerifyAsync(
            harness.Release("0.7.0", includeInstallerAsset: false), CancellationToken.None);

        Assert.Equal(UpdateDownloadState.Failed, status.DownloadState);
    }

    [Fact]
    public async Task Download_RejectsOversizedAsset()
    {
        var installerBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerBytes)).ToLowerInvariant();
        using var harness = new UpdateServiceHarness(
            new UpdateOptions { CurrentVersionOverride = "0.6.42", MaxInstallerBytes = 4 },
            request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                ? TextResponse(digest + "  ZoomCheck-Setup-x64.exe\n")
                : BinaryResponse(installerBytes));

        var status = await harness.Service.DownloadAndVerifyAsync(
            harness.Release("0.7.0"), CancellationToken.None);

        Assert.Equal(UpdateDownloadState.Failed, status.DownloadState);
        Assert.False(File.Exists(Path.Combine(harness.DownloadDirectory, "ZoomCheck-Setup-x64.exe")));
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage TextResponse(string text)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(text, System.Text.Encoding.UTF8, "text/plain")
        };

    private static HttpResponseMessage BinaryResponse(byte[] bytes)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
}

internal sealed class UpdateServiceHarness : IDisposable
{
    private readonly DirectoryInfo _directory;

    public UpdateServiceHarness(
        UpdateOptions options,
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        _directory = Directory.CreateTempSubdirectory("zoomcheck-updates");
        var handler = new StubHandler(responder ?? (_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
        }));
        Service = new UpdateService(
            new HttpClient(handler),
            Options.Create(options),
            new StubLifetime(),
            TimeProvider.System,
            NullLogger<UpdateService>.Instance,
            _directory.FullName);
    }

    public UpdateService Service { get; }

    public string DownloadDirectory => _directory.FullName;

    public UpdateService.GitHubRelease Release(
        string version,
        bool includeInstallerAsset = true,
        bool includeChecksumAsset = true)
    {
        Assert.True(SemanticVersion.TryParse(version, out var parsed));
        var assets = new List<UpdateService.GitHubAsset>();
        if (includeInstallerAsset)
        {
            assets.Add(new UpdateService.GitHubAsset(
                "ZoomCheck-Setup-x64.exe",
                "https://example.invalid/download/ZoomCheck-Setup-x64.exe",
                0));
        }

        if (includeChecksumAsset)
        {
            assets.Add(new UpdateService.GitHubAsset(
                "SHA256SUMS.txt",
                "https://example.invalid/download/SHA256SUMS.txt",
                0));
        }

        return new UpdateService.GitHubRelease(parsed!, parsed!.IsPrerelease, version, null, null, assets);
    }

    public void Dispose()
    {
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Temporary directory cleanup is best effort.
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
