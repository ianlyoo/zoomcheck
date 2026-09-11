using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Tests;

/// <summary>
/// Regression coverage for late-start recovery when Zoom OAuth is unconfigured.
/// The service previously fell back to the "me" user and issued /users/me/meetings,
/// which returned 401 and was reported as a Zoom failure instead of missing config.
/// These tests assert no outbound request is attempted, using a counting handler.
/// </summary>
public sealed class ZoomRecoveryServiceConfigurationGuardTests : IDisposable
{
    private readonly List<string> _databasePaths = new();

    [Theory]
    // Blank credentials.
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("   ", "   ", "   ")]
    // The placeholders shipped in appsettings.json.
    [InlineData("replace-with-your-zoom-s2s-client-id", "replace-with-your-zoom-s2s-client-secret", "replace-with-your-zoom-account-id")]
    // Partially configured is still unconfigured; each missing field alone must guard.
    [InlineData(null, "secret", "account")]
    [InlineData("client", null, "account")]
    [InlineData("client", "secret", null)]
    [InlineData("replace-with-your-zoom-s2s-client-id", "secret", "account")]
    public async Task RecoverLiveMeetingsAsync_MakesNoOutboundRequest_WhenOAuthIsNotConfigured(
        string? clientId,
        string? clientSecret,
        string? accountId)
    {
        var handler = new CountingHandler();
        var service = CreateService(
            handler,
            clientId: clientId,
            clientSecret: clientSecret,
            accountId: accountId);

        var result = await service.RecoverLiveMeetingsAsync();

        // The core assertion: the guard runs before any HTTP is attempted.
        Assert.Equal(0, handler.SendCount);
        Assert.Empty(handler.RequestedUris);

        Assert.False(result.Executed);
        Assert.Null(result.Error);
        Assert.Equal(0, result.UsersDiscovered);
        Assert.Equal(0, result.MeetingsDiscovered);
        Assert.Equal(0, result.AddedParticipants);
        Assert.Empty(result.Meetings);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Zoom:ClientId", warning);
        Assert.Contains("Zoom:ClientSecret", warning);
        Assert.Contains("Zoom:AccountId", warning);
        // The old behavior leaked transport detail into the operator-facing warning.
        Assert.DoesNotContain("401", warning);
        Assert.DoesNotContain("/users/me/meetings", warning);
    }

    [Fact]
    public async Task RecoverLiveMeetingsAsync_DoesNotCallZoom_EvenWhenFallbackMeUserIsEnabled()
    {
        // IncludeFallbackMeUser was the path that produced the /users/me/meetings 401.
        var handler = new CountingHandler();
        var service = CreateService(handler, clientId: null, clientSecret: null, accountId: null, includeFallbackMeUser: true);

        var result = await service.RecoverLiveMeetingsAsync();

        Assert.Equal(0, handler.SendCount);
        Assert.False(result.Executed);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RecoverLiveMeetingsAsync_DoesNotCallZoom_EvenWhenAccountWideDiscoveryIsEnabled()
    {
        // Account-wide discovery calls /users before meetings, so it must also be guarded.
        var handler = new CountingHandler();
        var service = CreateService(
            handler,
            clientId: null,
            clientSecret: null,
            accountId: null,
            enableAccountWideUserDiscovery: true);

        var result = await service.RecoverLiveMeetingsAsync();

        Assert.Equal(0, handler.SendCount);
        Assert.False(result.Executed);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RecoverLiveMeetingsAsync_RecordsGuardOutcomeAsLastRun()
    {
        var handler = new CountingHandler();
        var service = CreateService(handler, clientId: null, clientSecret: null, accountId: null);

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        var result = await service.RecoverLiveMeetingsAsync();
        var after = DateTimeOffset.UtcNow.AddSeconds(5);

        var lastRun = service.GetLastRun();

        Assert.NotNull(lastRun.LastResult);
        Assert.Same(result, lastRun.LastResult);
        Assert.NotNull(lastRun.LastRunAt);
        Assert.InRange(lastRun.LastRunAt!.Value, before, after);
    }

    [Fact]
    public async Task GetLastRun_ReportsNoRun_BeforeFirstInvocation()
    {
        var service = CreateService(new CountingHandler(), clientId: null, clientSecret: null, accountId: null);

        var lastRun = service.GetLastRun();

        Assert.Null(lastRun.LastRunAt);
        Assert.Null(lastRun.LastResult);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RecoverLiveMeetingsAsync_AttemptsOutboundRequest_WhenOAuthIsConfigured()
    {
        // Proves the guard is not unconditional. Credentials are fully populated, so the
        // service proceeds: it requests an OAuth token, then lists meetings for "me".
        var handler = new CountingHandler(ZoomResponses);
        var service = CreateService(
            handler,
            clientId: "real-client-id",
            clientSecret: "real-client-secret",
            accountId: "real-account-id");

        var result = await service.RecoverLiveMeetingsAsync();

        Assert.True(handler.SendCount > 0);
        Assert.Contains(handler.RequestedUris, uri => uri.Contains("/users/me/meetings", StringComparison.Ordinal));
        Assert.True(result.Executed);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task RecoverLiveMeetingsAsync_RequestsOAuthTokenBeforeApiCall_WhenConfigured()
    {
        var handler = new CountingHandler(ZoomResponses);
        var service = CreateService(
            handler,
            clientId: "real-client-id",
            clientSecret: "real-client-secret",
            accountId: "real-account-id");

        await service.RecoverLiveMeetingsAsync();

        Assert.Contains("zoom.us/oauth/token", handler.RequestedUris[0]);
    }

    // Minimal Zoom responses: an OAuth token, then an empty live-meetings page.
    private static HttpResponseMessage ZoomResponses(HttpRequestMessage request)
    {
        var uri = request.RequestUri?.ToString() ?? string.Empty;

        var json = uri.Contains("oauth/token", StringComparison.Ordinal)
            ? "{\"access_token\":\"stub-token\",\"expires_in\":3600}"
            : "{\"page_count\":1,\"meetings\":[]}";

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private ZoomRecoveryService CreateService(
        CountingHandler handler,
        string? clientId,
        string? clientSecret,
        string? accountId,
        bool includeFallbackMeUser = true,
        bool enableAccountWideUserDiscovery = false)
    {
        // Fully qualified: "using ZoomCheck.Backend.Options" shadows the unqualified
        // Options.Create from Microsoft.Extensions.Options.
        var zoomOptions = Microsoft.Extensions.Options.Options.Create(new ZoomOptions
        {
            ClientId = clientId ?? string.Empty,
            ClientSecret = clientSecret ?? string.Empty,
            AccountId = accountId ?? string.Empty
        });

        var recoveryOptions = Microsoft.Extensions.Options.Options.Create(new ZoomRecoveryOptions
        {
            Enabled = true,
            IncludeFallbackMeUser = includeFallbackMeUser,
            EnableAccountWideUserDiscovery = enableAccountWideUserDiscovery,
            HostUserIds = Array.Empty<string>(),
            UsersPageSize = 100,
            MeetingsPageSize = 300,
            ParticipantsPageSize = 300
        });

        // One HttpClient over the counting handler serves both the token service and the
        // API client, so any outbound attempt from either is observed.
        var httpClient = new HttpClient(handler);
        var tokenService = new ZoomOAuthTokenService(httpClient, zoomOptions);
        var apiClient = new ZoomApiClient(httpClient, tokenService, recoveryOptions);

        return new ZoomRecoveryService(apiClient, CreateAttendanceService(), recoveryOptions);
    }

    private AttendanceApplicationService CreateAttendanceService()
    {
        // A unique temp database keeps each test isolated. The guard path never touches
        // it, and the configured path only reads an empty meeting.
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "zoomcheck-tests",
            Guid.NewGuid().ToString("N"),
            "recovery-tests.db");

        _databasePaths.Add(databasePath);

        var repository = new SqliteAttendanceRepository(databasePath);
        repository.InitializeAsync().GetAwaiter().GetResult();

        return new AttendanceApplicationService(new ExcelRosterParser(), repository, new AttendanceMatcher());
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections, so the database file stays locked
        // after the repository is done with it. Without this, every deletion below
        // fails with IOException and temp directories accumulate across runs.
        SqliteConnection.ClearAllPools();

        foreach (var databasePath in _databasePaths)
        {
            try
            {
                var directory = Path.GetDirectoryName(databasePath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp file must never fail the suite.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Records every outbound attempt. Returns 503 by default so an unexpected call
    /// is visible as a counted request rather than silently succeeding.
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _responder;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        {
            _responder = responder;
        }

        public int SendCount { get; private set; }

        public List<string> RequestedUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            RequestedUris.Add(request.RequestUri?.ToString() ?? string.Empty);

            var response = _responder?.Invoke(request)
                ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("unexpected outbound request", Encoding.UTF8, "application/json")
                };

            return Task.FromResult(response);
        }
    }
}
