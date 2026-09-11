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
    public async Task SavedUrlPendingRestart_RejectsCodesForTheOldRelay()
    {
        using var fixture = new PairingFixture("https://old.example/", true);
        await fixture.Settings.SaveAsync("https://new.example/", CancellationToken.None);

        var response = await fixture.Controller.CreatePairingCode(CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Contains("Restart", Assert.IsType<ProblemDetails>(problem.Value).Title);
        Assert.Equal(0, fixture.Handler.Calls);
        Assert.Null(fixture.Bridge.GetStatus().PairingCodeExpiresAt);
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
            Http = new HttpClient(Handler);
            Relay = new ZoomRelayService(new ZoomRelayClient(Http, options), Bridge, options, TimeProvider.System);
            Settings = new ZoomRelaySettingsStore(_settingsPath, baseUrl, enabled);
            Controller = new ZoomAppBridgeController(Bridge, Relay, attendance, Settings);
            Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        }

        public RegistrationHandler Handler { get; } = new();
        private HttpClient Http { get; }
        private ZoomRelayService Relay { get; }
        public ZoomAppBridgeService Bridge { get; }
        public ZoomAppBridgeController Controller { get; }
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            RegisteredCode = (await request.Content!.ReadFromJsonAsync<CreatePairingRequest>(cancellationToken))!.PairingCode;
            return new HttpResponseMessage(StatusCode)
            {
                Content = JsonContent.Create(new CreatePairingResponse("test-session", "test-token",
                    Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow.AddMinutes(10)))
            };
        }
    }
}
