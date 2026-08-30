using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using ZoomCheck.Relay.Contracts;
using ZoomCheck.Relay.Tests.Support;

namespace ZoomCheck.Relay.Tests;

public sealed class RelayHttpIntegrationTests
{
    [Fact]
    public async Task HealthAndStaticCompanion_AreServedWithSecurityHeaders()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();

        using var health = await client.GetAsync("/health");
        using var companion = await client.GetAsync("/zoom-app/");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, companion.StatusCode);
        Assert.Contains("ZoomCheck", await companion.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("appssdk.zoom.us", companion.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", companion.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer-when-downgrade", companion.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task ProductionHttps_UsesZoomRequiredHstsPolicy()
    {
        await using var app = await StartAsync(Environments.Production);
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://zoomcheck-relay.example");

        using var companion = await client.GetAsync("/zoom-app/");

        Assert.Equal(HttpStatusCode.OK, companion.StatusCode);
        Assert.Equal(
            "max-age=31536000; includeSubDomains",
            companion.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task PairRedeemPublishAndPoll_UsesBearerProtectedDirectionalQueues()
    {
        await using var app = await StartAsync();
        var client = app.GetTestClient();
        var created = await Post<CreatePairingResponse>(client, "/api/v1/pairings",
            new CreatePairingRequest("654321", RelayTestFixtures.PublicKey()));
        var redeemed = await Post<RedeemPairingResponse>(client, "/api/v1/pairings/redeem",
            new RedeemPairingRequest("654321", RelayTestFixtures.PublicKey()));

        using var unauthorized = await client.GetAsync(
            $"/api/v1/sessions/{created.SessionId}/desktop/messages?afterSequence=0");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var envelope = RelayTestFixtures.Envelope(1);
        using var post = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/sessions/{created.SessionId}/companion/messages");
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", redeemed.CompanionToken);
        post.Content = JsonContent.Create(envelope);
        using var accepted = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var poll = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/sessions/{created.SessionId}/desktop/messages?afterSequence=0");
        poll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", created.DesktopToken);
        using var received = await client.SendAsync(poll);
        var payload = await received.Content.ReadFromJsonAsync<RelayMessagesResponse>();

        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        Assert.True(payload!.Connected);
        Assert.Single(payload.Messages);
        Assert.Equal(envelope.Ciphertext, payload.Messages[0].Ciphertext);
    }

    private static async Task<WebApplication> StartAsync(string environmentName = "Development")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
            ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ZoomCheck.Relay"))
        });
        builder.WebHost.UseTestServer();
        var app = RelayApp.Build(builder);
        await app.StartAsync();
        return app;
    }

    private static async Task<T> Post<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
