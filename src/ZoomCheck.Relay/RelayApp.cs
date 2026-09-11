using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZoomCheck.Relay.Endpoints;
using ZoomCheck.Relay.Middleware;
using ZoomCheck.Relay.Options;
using ZoomCheck.Relay.Services;

namespace ZoomCheck.Relay;

/// <summary>
/// Composition root, shared by <c>Program</c> and the test host so tests exercise the same
/// pipeline and limits that run in production.
/// </summary>
public static class RelayApp
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        builder.Configuration.AddEnvironmentVariables(prefix: "ZOOMCHECK_RELAY_");
        builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));

        var limits = (builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>()
            ?? new RelayOptions()).Normalized();

        // Body size is capped at the server level so an oversized upload is rejected before
        // any relay code allocates for it.
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
            kestrel.AddServerHeader = false;
        });

        builder.Services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        builder.Services.AddHsts(options =>
        {
            // Zoom Apps validates the Home URL against its OWASP header profile.
            options.MaxAge = TimeSpan.FromDays(365);
            options.IncludeSubDomains = true;
        });

        // TryAdd so a test host (or an embedding host) can supply a controllable clock.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RelaySessionStore>();
        builder.Services.AddSingleton<RelayRateLimiter>();
        builder.Services.AddRouting();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
            };
            forwarded.KnownNetworks.Clear();
            forwarded.KnownProxies.Clear();
            app.UseForwardedHeaders(forwarded);
            app.UseHsts();
        }

        app.UseMiddleware<RelaySecurityHeadersMiddleware>();
        app.UseMiddleware<RelayRateLimitMiddleware>();

        ConfigureCompanionAssets(app);

        app.MapRelayEndpoints();
        app.MapGet("/health", (RelaySessionStore store, TimeProvider clock) =>
            Results.Ok(new ZoomCheck.Relay.Contracts.RelayHealthResponse(
                "ok", store.ActiveSessionCount, store.Options.MaxSessions, clock.GetUtcNow())));
        return app;
    }

    /// <summary>
    /// Serves the Zoom companion bundle from <c>wwwroot/zoom-app</c> when present.
    /// </summary>
    /// <remarks>
    /// The bundle is copied into this project's <c>wwwroot/zoom-app</c> as a build-time asset; the
    /// relay never reads from the Backend project at runtime, so the two deploy independently. If
    /// the directory is absent (for example a contracts-only CI job), static hosting is skipped
    /// rather than failing startup, and the API surface remains fully functional.
    /// </remarks>
    private static void ConfigureCompanionAssets(WebApplication app)
    {
        var root = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "zoom-app");
        if (!Directory.Exists(root))
        {
            app.Logger.LogInformation("Companion asset directory not present; serving API only.");
            return;
        }

        var provider = new PhysicalFileProvider(root);
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = provider,
            RequestPath = "/zoom-app",
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            RequestPath = "/zoom-app",
            ServeUnknownFileTypes = false,
        });
    }
}
