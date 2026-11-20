using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend;
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

    [Theory]
    [InlineData("0.7.0-rc.1", true, true, false)]
    [InlineData("0.6.3", false, false, false)]
    [InlineData("0.6.1", false, true, false)]
    [InlineData(null, false, true, false)]
    [InlineData("0.6.2", false, true, true)]
    public async Task Check_ReadinessBelongsOnlyToTheSelectedNewerRelease(
        string? nextVersion, bool prerelease, bool automaticDownload, bool expectedReady)
    {
        var installerBytes = new byte[] { 1, 2, 3, 4, 5 };
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerBytes));
        var releases = """[{"tag_name":"v0.6.2","prerelease":false,"assets":[]}]""";
        var assetRequests = 0;
        using var harness = new UpdateServiceHarness(
            new UpdateOptions
            {
                CurrentVersionOverride = "0.6.1",
                AllowPrerelease = true,
                AutomaticDownload = automaticDownload
            },
            request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
                {
                    return JsonResponse(releases);
                }

                assetRequests++;
                return request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
                    ? TextResponse(digest + "  ZoomCheck-Setup-x64.exe\n")
                    : BinaryResponse(installerBytes);
            });

        // Stage inert bytes directly so this regression also runs on non-Windows
        // hosts. All HTTP responses are mocked; no installer is ever launched.
        await harness.Service.DownloadAndVerifyAsync(harness.Release("0.6.2"), CancellationToken.None);
        var initial = await harness.Service.CheckAsync(manual: false, CancellationToken.None);
        Assert.Equal("0.6.2", initial.LatestVersion);
        Assert.True(initial.InstallerReady);

        releases = nextVersion is null ? "[]" : System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { tag_name = "v" + nextVersion, prerelease, assets = Array.Empty<object>() }
        });
        var status = await harness.Service.CheckAsync(manual: true, CancellationToken.None);

        Assert.Equal(nextVersion, status.LatestVersion);
        Assert.Equal(expectedReady, status.InstallerReady);
        Assert.Equal(expectedReady ? UpdateDownloadState.Verified : UpdateDownloadState.None, status.DownloadState);
        Assert.Equal(expectedReady ? "ZoomCheck-Setup-x64.exe" : null, status.InstallerFileName);
        Assert.Equal(expectedReady ? (long?)installerBytes.Length : null, status.InstallerSizeBytes);
        Assert.Equal(expectedReady, harness.Service.GetStatus().InstallerReady);
        Assert.Equal(2, assetRequests);
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

    [Fact]
    public async Task ConcurrentChecksAndStatusReads_PreserveConsistentState()
    {
        var releaseRequests = 0;
        using var harness = new UpdateServiceHarness(
            new UpdateOptions
            {
                CurrentVersionOverride = "0.6.3",
                AutomaticDownload = false
            },
            _ =>
            {
                Interlocked.Increment(ref releaseRequests);
                return JsonResponse("""[{"tag_name":"v0.7.0","name":"v0.7.0","draft":false,"prerelease":false,"assets":[]}]""");
            });

        var checks = Enumerable.Range(0, 6)
            .Select(_ => harness.Service.CheckAsync(manual: true, CancellationToken.None))
            .ToArray();
        var reads = Enumerable.Range(0, 12)
            .Select(_ => Task.Run(() => harness.Service.GetStatus()))
            .ToArray();

        var checkResults = await Task.WhenAll(checks);
        var readResults = await Task.WhenAll(reads);

        Assert.Equal(6, releaseRequests);
        Assert.All(checkResults, status =>
        {
            Assert.Equal(UpdateAvailability.UpdateAvailable, status.Availability);
            Assert.Equal("0.7.0", status.LatestVersion);
            Assert.NotNull(status.LastCheckedAt);
        });
        Assert.All(readResults, status => Assert.Equal("0.6.3", status.CurrentVersion));

        var finalStatus = harness.Service.GetStatus();
        Assert.Equal(UpdateAvailability.UpdateAvailable, finalStatus.Availability);
        Assert.Equal("0.7.0", finalStatus.LatestVersion);
        Assert.NotNull(finalStatus.LastCheckedAt);
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

public sealed class UpdateServiceDependencyInjectionTests
{
    private static readonly JsonSerializerOptions StatusJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string ReleaseFeedPayload = """
    [
      {
        "tag_name": "v0.7.0",
        "name": "ZoomCheck v0.7.0",
        "body": "Release notes",
        "draft": false,
        "prerelease": false,
        "published_at": "2026-11-20T04:00:00Z",
        "assets": []
      }
    ]
    """;

    [Fact]
    public async Task RealDiContainer_SharesOneUpdateServiceAcrossResolutionsHostedServiceAndController()
    {
        using var tempDir = new TempTestDirectory();
        await using var app = BuildBackendTestApp(
            tempDir.Path,
            checkOnStartup: false,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseFeedPayload, System.Text.Encoding.UTF8, "application/json")
            });
        await app.StartAsync();

        var first = app.Services.GetRequiredService<UpdateService>();
        var second = app.Services.GetRequiredService<UpdateService>();
        Assert.Same(first, second);

        using var scopeA = app.Services.CreateScope();
        using var scopeB = app.Services.CreateScope();
        var controllerA = ActivatorUtilities.CreateInstance<UpdatesController>(scopeA.ServiceProvider);
        var controllerB = ActivatorUtilities.CreateInstance<UpdatesController>(scopeB.ServiceProvider);
        var hostedService = Assert.Single(app.Services.GetServices<IHostedService>().OfType<UpdateBackgroundService>());

        Assert.Same(first, controllerA.UpdateService);
        Assert.Same(first, controllerB.UpdateService);
        Assert.Same(first, hostedService.UpdateService);

        var checkResult = await first.CheckAsync(manual: true, CancellationToken.None);
        Assert.Equal(UpdateAvailability.UpdateAvailable, checkResult.Availability);
        Assert.Equal("0.7.0", checkResult.LatestVersion);
        Assert.NotNull(checkResult.LastCheckedAt);

        var secondStatus = second.GetStatus();
        var controllerStatus = Assert.IsType<UpdateStatusResponse>(
            Assert.IsType<OkObjectResult>(controllerB.GetStatus().Result).Value);

        Assert.Equal(UpdateAvailability.UpdateAvailable, secondStatus.Availability);
        Assert.Equal("0.7.0", secondStatus.LatestVersion);
        Assert.Equal(checkResult.LastCheckedAt, secondStatus.LastCheckedAt);
        Assert.Equal(UpdateAvailability.UpdateAvailable, controllerStatus.Availability);
        Assert.Equal("0.7.0", controllerStatus.LatestVersion);
        Assert.Equal(checkResult.LastCheckedAt, controllerStatus.LastCheckedAt);

        var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/update/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var httpStatus = await response.Content.ReadFromJsonAsync<UpdateStatusResponse>(StatusJsonOptions);

        Assert.NotNull(httpStatus);
        Assert.Equal(UpdateAvailability.UpdateAvailable, httpStatus!.Availability);
        Assert.Equal("0.7.0", httpStatus.LatestVersion);
        Assert.Equal(checkResult.LastCheckedAt, httpStatus.LastCheckedAt);
    }

    [Fact]
    public async Task BackgroundStartupCheck_ResultIsReturnedByGetStatusEndpoint()
    {
        using var tempDir = new TempTestDirectory();
        var downloadDir = Path.Combine(tempDir.Path, "updates");
        await using var app = BuildBackendTestApp(
            tempDir.Path,
            checkOnStartup: true,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseFeedPayload, System.Text.Encoding.UTF8, "application/json")
            },
            configureServices: services =>
            {
                // On non-Windows CI/dev hosts SupportedPlatform is false, so force it true here
                // to exercise UpdateBackgroundService's full startup loop on every OS while keeping
                // the singleton + named IHttpClientFactory wiring intact.
                if (!OperatingSystem.IsWindows())
                {
                    services.Replace(ServiceDescriptor.Singleton(sp => new UpdateService(
                        sp.GetRequiredService<IHttpClientFactory>(),
                        sp.GetRequiredService<IOptions<UpdateOptions>>(),
                        sp.GetRequiredService<IHostApplicationLifetime>(),
                        sp.GetRequiredService<TimeProvider>(),
                        sp.GetRequiredService<ILogger<UpdateService>>(),
                        downloadDir,
                        supportedPlatform: true)));
                }
            });

        await app.StartAsync();

        var client = app.GetTestClient();
        UpdateStatusResponse? status = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/api/update/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            status = await response.Content.ReadFromJsonAsync<UpdateStatusResponse>(StatusJsonOptions);
            if (status?.LastCheckedAt is not null)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(status);
        Assert.True(status!.Enabled);
        Assert.True(status.SupportedPlatform);
        Assert.Equal("0.6.3", status.CurrentVersion);
        Assert.Equal("0.7.0", status.LatestVersion);
        Assert.Equal(UpdateAvailability.UpdateAvailable, status.Availability);
        Assert.Equal(UpdateDownloadState.None, status.DownloadState);
        Assert.NotNull(status.LastCheckedAt);
    }

    [Fact]
    public async Task PostCheck_StatePersistsToSubsequentGetStatusRequest()
    {
        using var tempDir = new TempTestDirectory();
        await using var app = BuildBackendTestApp(
            tempDir.Path,
            checkOnStartup: false,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseFeedPayload, System.Text.Encoding.UTF8, "application/json")
            });
        await app.StartAsync();

        var client = app.GetTestClient();

        using var checkResponse = await client.PostAsJsonAsync("/api/update/check", new { });
        Assert.Equal(HttpStatusCode.OK, checkResponse.StatusCode);
        var checkedStatus = await checkResponse.Content.ReadFromJsonAsync<UpdateStatusResponse>(StatusJsonOptions);
        Assert.NotNull(checkedStatus);
        Assert.Equal(UpdateAvailability.UpdateAvailable, checkedStatus!.Availability);
        Assert.Equal("0.7.0", checkedStatus.LatestVersion);
        Assert.NotNull(checkedStatus.LastCheckedAt);

        using var statusResponse = await client.GetAsync("/api/update/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var persistedStatus = await statusResponse.Content.ReadFromJsonAsync<UpdateStatusResponse>(StatusJsonOptions);
        Assert.NotNull(persistedStatus);
        Assert.Equal(UpdateAvailability.UpdateAvailable, persistedStatus!.Availability);
        Assert.Equal("0.7.0", persistedStatus.LatestVersion);
        Assert.Equal(checkedStatus.LastCheckedAt, persistedStatus.LastCheckedAt);
    }

    [Fact]
    public async Task BackgroundStagedDownload_IsVisibleToGetStatusAcrossRequests()
    {
        using var tempDir = new TempTestDirectory();
        var downloadDir = Path.Combine(tempDir.Path, "updates");
        var installerBytes = new byte[] { 10, 20, 30, 40, 50 };
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerBytes)).ToLowerInvariant();
        var feedWithAssets = """
        [
          {
            "tag_name": "v0.7.0",
            "name": "ZoomCheck v0.7.0",
            "draft": false,
            "prerelease": false,
            "assets": [
              {
                "name": "ZoomCheck-Setup-x64.exe",
                "browser_download_url": "https://example.invalid/download/ZoomCheck-Setup-x64.exe",
                "size": 5
              },
              {
                "name": "SHA256SUMS.txt",
                "browser_download_url": "https://example.invalid/download/SHA256SUMS.txt",
                "size": 90
              }
            ]
          }
        ]
        """;

        await using var app = BuildBackendTestApp(
            tempDir.Path,
            checkOnStartup: true,
            request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(feedWithAssets, System.Text.Encoding.UTF8, "application/json")
                    };
                }

                if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(digest + "  ZoomCheck-Setup-x64.exe\n", System.Text.Encoding.UTF8, "text/plain")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(installerBytes)
                };
            },
            automaticDownload: true,
            configureServices: services =>
            {
                services.Replace(ServiceDescriptor.Singleton(sp => new UpdateService(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IOptions<UpdateOptions>>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILogger<UpdateService>>(),
                    downloadDir,
                    supportedPlatform: true)));
            });

        await app.StartAsync();

        var client = app.GetTestClient();
        UpdateStatusResponse? status = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/api/update/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            status = await response.Content.ReadFromJsonAsync<UpdateStatusResponse>(StatusJsonOptions);
            if (status?.InstallerReady == true)
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.NotNull(status);
        Assert.Equal(UpdateAvailability.UpdateAvailable, status!.Availability);
        Assert.Equal(UpdateDownloadState.Verified, status.DownloadState);
        Assert.True(status.InstallerReady);
        Assert.Equal("ZoomCheck-Setup-x64.exe", status.InstallerFileName);
        Assert.Equal(installerBytes.Length, status.InstallerSizeBytes);
    }

    private static WebApplication BuildBackendTestApp(
        string tempDirectory,
        bool checkOnStartup,
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        bool automaticDownload = false,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BackendApp).Assembly.GetName().Name,
            EnvironmentName = Environments.Production,
            ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ZoomCheck.Backend"))
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DatabasePath"] = Path.Combine(tempDirectory, "zoomcheck.db"),
            ["Dashboard:OpenBrowserOnStart"] = "false",
            ["ZoomRelay:Enabled"] = "false",
            ["ZoomRecovery:Enabled"] = "false",
            ["Update:Enabled"] = "true",
            ["Update:CheckOnStartup"] = checkOnStartup ? "true" : "false",
            ["Update:StartupDelaySeconds"] = "0",
            ["Update:CheckIntervalHours"] = "0",
            ["Update:AutomaticDownload"] = automaticDownload ? "true" : "false",
            ["Update:CurrentVersionOverride"] = "0.6.3"
        });

        var handler = new DelegateHttpMessageHandler(responder);
        return BackendApp.Build(builder, services =>
        {
            services.AddHttpClient(UpdateService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler);
            configureServices?.Invoke(services);
        });
    }

    private sealed class DelegateHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class TempTestDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("zoomcheck-di-tests");

        public string Path => _directory.FullName;

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
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
            new StubHttpClientFactory(handler),
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

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false);
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
