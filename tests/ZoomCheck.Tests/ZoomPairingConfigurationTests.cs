using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Controllers;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;
using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Tests;

public sealed class ZoomPairingConfigurationTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("http://relay.example/", true)]
    [InlineData("https://relay.example/", false)]
    public async Task NoUsableRelayOrDirectHome_RejectsWithoutCreatingLocalCode(string baseUrl, bool enabled)
    {
        using var fixture = new PairingFixture(baseUrl, enabled);

        var response = await fixture.Controller.CreatePairingCode(CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Contains("not configured", Assert.IsType<ProblemDetails>(problem.Value).Title);
        Assert.Null(fixture.Bridge.GetStatus().PairingCodeExpiresAt);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task ChangingSavedRelayUrl_AppliesToNextPairingRequestWithoutRestart()
    {
        using var fixture = new PairingFixture("https://old.example/", true);

        var firstResponse = await fixture.Controller.CreatePairingCode(CancellationToken.None);
        var firstCode = Assert.IsType<ZoomAppPairingCodeResponse>(Assert.IsType<OkObjectResult>(firstResponse.Result).Value);
        Assert.Equal("https://old.example/api/v1/pairings", fixture.Handler.LastUri?.AbsoluteUri);
        Assert.Equal("https://old.example/zoom-app/", firstCode.HomeUrl);
        Assert.NotNull(fixture.Relay.GetStatus().PairingCodeExpiresAt);

        var saved = await fixture.Settings.SaveAsync("https://new.example/zoom-app/", CancellationToken.None);
        Assert.True(saved.Configured);
        Assert.False(saved.RestartRequired);
        // Changing the relay clears the previous relay's pending session immediately.
        Assert.Null(fixture.Relay.GetStatus().PairingCodeExpiresAt);
        Assert.Equal("https://new.example/zoom-app/", fixture.Relay.GetStatus().HomeUrl);

        var secondResponse = await fixture.Controller.CreatePairingCode(CancellationToken.None);
        var secondCode = Assert.IsType<ZoomAppPairingCodeResponse>(Assert.IsType<OkObjectResult>(secondResponse.Result).Value);
        Assert.Equal("https://new.example/api/v1/pairings", fixture.Handler.LastUri?.AbsoluteUri);
        Assert.Equal(fixture.Handler.RegisteredCode, secondCode.Code);
        Assert.Equal("https://new.example/zoom-app/", secondCode.HomeUrl);
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public async Task SavingRelayUrlFromEmptyStart_EnablesPairingWithoutRestart()
    {
        using var fixture = new PairingFixture("", true);

        var before = await fixture.Controller.CreatePairingCode(CancellationToken.None);
        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(before.Result).StatusCode);

        await fixture.Settings.SaveAsync("https://fresh.example/", CancellationToken.None);

        var after = await fixture.Controller.CreatePairingCode(CancellationToken.None);
        var code = Assert.IsType<ZoomAppPairingCodeResponse>(Assert.IsType<OkObjectResult>(after.Result).Value);
        Assert.Equal("https://fresh.example/api/v1/pairings", fixture.Handler.LastUri?.AbsoluteUri);
        Assert.Equal("https://fresh.example/zoom-app/", code.HomeUrl);
    }

    [Fact]
    public async Task ClearingRelayUrlViaPut_ReturnsUnconfiguredAndClearsActiveRelayState()
    {
        using var fixture = new PairingFixture("https://relay.example/", true);

        var created = await fixture.Controller.CreatePairingCode(CancellationToken.None);
        Assert.IsType<OkObjectResult>(created.Result);
        Assert.NotNull(fixture.Relay.GetStatus().PairingCodeExpiresAt);

        var clearedResponse = await fixture.SettingsController.Put(
            new UpdateZoomRelaySettingsRequest(""), CancellationToken.None);
        var cleared = Assert.IsType<ZoomRelaySettingsResponse>(
            Assert.IsType<OkObjectResult>(clearedResponse.Result).Value);
        Assert.False(cleared.Configured);
        Assert.Equal(string.Empty, cleared.BaseUrl);
        Assert.False(cleared.RestartRequired);

        var readBack = Assert.IsType<ZoomRelaySettingsResponse>(
            Assert.IsType<OkObjectResult>(fixture.SettingsController.Get().Result).Value);
        Assert.Equal(cleared, readBack);
        Assert.False(fixture.Relay.IsConfigured);
        Assert.Null(fixture.Relay.GetStatus().PairingCodeExpiresAt);
        Assert.Null(fixture.Relay.GetStatus().HomeUrl);

        var pairingAfterClear = await fixture.Controller.CreatePairingCode(CancellationToken.None);
        Assert.Equal(StatusCodes.Status409Conflict,
            Assert.IsType<ObjectResult>(pairingAfterClear.Result).StatusCode);

        var healthAfterClear = Assert.IsType<ZoomRelayHealthResponse>(
            Assert.IsType<OkObjectResult>((await fixture.SettingsController.CheckHealth(
                new CheckZoomRelayHealthRequest(null), CancellationToken.None)).Result).Value);
        Assert.False(healthAfterClear.Ok);
        Assert.Equal("not_configured", healthAfterClear.ErrorCode);
    }

    [Fact]
    public async Task HealthCheck_ReportsHealthyRelayAndClearFailureReasons()
    {
        using var fixture = new PairingFixture("https://relay.example/", true);

        var okResult = await fixture.SettingsController.CheckHealth(
            new CheckZoomRelayHealthRequest(null), CancellationToken.None);
        var ok = Assert.IsType<ZoomRelayHealthResponse>(Assert.IsType<OkObjectResult>(okResult.Result).Value);
        Assert.True(ok.Ok);
        Assert.Equal("https://relay.example/", ok.BaseUrl);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal(2, ok.ActiveSessions);
        Assert.Equal(500, ok.MaxSessions);
        Assert.Equal("https://relay.example/health", fixture.Handler.LastUri?.AbsoluteUri);

        var invalidResult = await fixture.SettingsController.CheckHealth(
            new CheckZoomRelayHealthRequest("http://insecure.example/"), CancellationToken.None);
        var invalid = Assert.IsType<ZoomRelayHealthResponse>(Assert.IsType<OkObjectResult>(invalidResult.Result).Value);
        Assert.False(invalid.Ok);
        Assert.Equal("invalid_url", invalid.ErrorCode);

        fixture.Handler.HealthResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var httpErrorResult = await fixture.SettingsController.CheckHealth(
            new CheckZoomRelayHealthRequest("https://waking.example/"), CancellationToken.None);
        var httpError = Assert.IsType<ZoomRelayHealthResponse>(Assert.IsType<OkObjectResult>(httpErrorResult.Result).Value);
        Assert.False(httpError.Ok);
        Assert.Equal("http_error", httpError.ErrorCode);
        Assert.Equal(503, httpError.StatusCode);
        Assert.Equal("https://waking.example/health", fixture.Handler.LastUri?.AbsoluteUri);

        fixture.Handler.HealthResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not a relay</html>")
        };
        var nonRelayResult = await fixture.SettingsController.CheckHealth(
            new CheckZoomRelayHealthRequest("https://other.example/"), CancellationToken.None);
        var nonRelay = Assert.IsType<ZoomRelayHealthResponse>(Assert.IsType<OkObjectResult>(nonRelayResult.Result).Value);
        Assert.False(nonRelay.Ok);
        Assert.Equal("invalid_response", nonRelay.ErrorCode);

        fixture.Handler.HealthResponseFactory = _ => throw new TaskCanceledException("timeout");
        var timeoutResult = await fixture.SettingsController.CheckHealth(
            new CheckZoomRelayHealthRequest("https://slow.example/"), CancellationToken.None);
        var timeout = Assert.IsType<ZoomRelayHealthResponse>(Assert.IsType<OkObjectResult>(timeoutResult.Result).Value);
        Assert.False(timeout.Ok);
        Assert.Equal("timeout", timeout.ErrorCode);

        fixture.Handler.HealthResponseFactory = _ => throw new HttpRequestException(
            HttpRequestError.NameResolutionError, "No such host");
        var dnsResult = await fixture.SettingsController.CheckHealth(
            new CheckZoomRelayHealthRequest("https://missing.example/"), CancellationToken.None);
        var dns = Assert.IsType<ZoomRelayHealthResponse>(Assert.IsType<OkObjectResult>(dnsResult.Result).Value);
        Assert.False(dns.Ok);
        Assert.Equal("dns_error", dns.ErrorCode);
    }

    [Fact]
    public async Task HealthCheck_UnconfiguredWithoutInputReturnsNotConfigured()
    {
        using var fixture = new PairingFixture("", true);

        var result = await fixture.SettingsController.CheckHealth(
            new CheckZoomRelayHealthRequest(null), CancellationToken.None);
        var health = Assert.IsType<ZoomRelayHealthResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(health.Ok);
        Assert.Equal("not_configured", health.ErrorCode);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task ConfiguredDirectHome_KeepsExplicitDirectBridgeCompatibility()
    {
        using var fixture = new PairingFixture("", true, "https://direct.example/zoom-app/");

        var response = await fixture.Controller.CreatePairingCode(CancellationToken.None);

        var code = Assert.IsType<ZoomAppPairingCodeResponse>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Matches("^[0-9]{6}$", code.Code);
        Assert.Equal("https://direct.example/zoom-app/", code.HomeUrl);
        Assert.NotNull(fixture.Bridge.GetStatus().PairingCodeExpiresAt);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task ConfiguredRelay_RegistersExactlyTheDisplayedCodeAtTheNormalizedOrigin()
    {
        using var fixture = new PairingFixture("https://relay.example/zoom-app/", true);

        var response = await fixture.Controller.CreatePairingCode(CancellationToken.None);

        var code = Assert.IsType<ZoomAppPairingCodeResponse>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal("https://relay.example/api/v1/pairings", fixture.Handler.LastUri?.AbsoluteUri);
        Assert.Equal(fixture.Handler.RegisteredCode, code.Code);
        Assert.Equal("https://relay.example/zoom-app/", code.HomeUrl);
        Assert.Null(fixture.Bridge.GetStatus().PairingCodeExpiresAt);
    }

    [Fact]
    public async Task UnavailableRelay_DoesNotFallBackToAnUnregisteredLocalCode()
    {
        using var fixture = new PairingFixture("https://relay.example/", true, "https://direct.example/zoom-app/");
        fixture.Handler.StatusCode = HttpStatusCode.ServiceUnavailable;

        var response = await fixture.Controller.CreatePairingCode(CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(response.Result).StatusCode);
        Assert.Null(fixture.Bridge.GetStatus().PairingCodeExpiresAt);
    }

    private sealed class PairingFixture : IDisposable
    {
        private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"pairing-settings-{Guid.NewGuid():N}", "appsettings.User.json");
        public PairingFixture(string baseUrl, bool enabled, string directHome = "")
        {
            var attendance = new AttendanceApplicationService(new ExcelRosterParser(),
                new SqliteAttendanceRepository(Path.Combine(Path.GetTempPath(), $"pairing-unused-{Guid.NewGuid():N}.db")),
                new AttendanceMatcher());
            Bridge = new ZoomAppBridgeService(attendance,
                Options.Create(new ZoomAppOptions { HomeUrl = directHome }), TimeProvider.System);
            var options = Options.Create(new ZoomRelayOptions { BaseUrl = baseUrl, Enabled = enabled });
            Settings = new ZoomRelaySettingsStore(_settingsPath, baseUrl, enabled);
            Http = new HttpClient(Handler);
            var client = new ZoomRelayClient(Http, options, Settings);
            Relay = new ZoomRelayService(client, Bridge, options, Settings, TimeProvider.System);
            Controller = new ZoomAppBridgeController(Bridge, Relay, attendance);
            Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            SettingsController = new ZoomRelaySettingsController(Settings, client);
            SettingsController.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        }

        public RegistrationHandler Handler { get; } = new();
        private HttpClient Http { get; }
        public ZoomRelayService Relay { get; }
        public ZoomAppBridgeService Bridge { get; }
        public ZoomAppBridgeController Controller { get; }
        public ZoomRelaySettingsController SettingsController { get; }
        public ZoomRelaySettingsStore Settings { get; }

        public void Dispose()
        {
            Relay.Dispose();
            Http.Dispose();
            if (Directory.Exists(Path.GetDirectoryName(_settingsPath)))
            {
                Directory.Delete(Path.GetDirectoryName(_settingsPath)!, recursive: true);
            }
        }
    }

    private sealed class RegistrationHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? RegisteredCode { get; private set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public Func<HttpRequestMessage, HttpResponseMessage>? HealthResponseFactory { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/health", StringComparison.Ordinal) == true)
            {
                if (HealthResponseFactory is not null)
                {
                    return HealthResponseFactory(request);
                }

                return new HttpResponseMessage(StatusCode)
                {
                    Content = JsonContent.Create(new RelayHealthResponse("ok", 2, 500, DateTimeOffset.UtcNow))
                };
            }

            RegisteredCode = (await request.Content!.ReadFromJsonAsync<CreatePairingRequest>(cancellationToken))!.PairingCode;
            return new HttpResponseMessage(StatusCode)
            {
                Content = JsonContent.Create(new CreatePairingResponse("test-session", "test-token",
                    Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow.AddMinutes(10)))
            };
        }
    }
}
