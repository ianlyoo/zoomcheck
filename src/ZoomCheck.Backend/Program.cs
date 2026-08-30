using System.Text.Json.Serialization;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Options;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "ZOOMCHECK_");

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<ZoomOptions>(builder.Configuration.GetSection(ZoomOptions.SectionName));
builder.Services.Configure<ZoomAppOptions>(builder.Configuration.GetSection(ZoomAppOptions.SectionName));
builder.Services.Configure<ZoomRecoveryOptions>(builder.Configuration.GetSection(ZoomRecoveryOptions.SectionName));
builder.Services.Configure<DashboardOptions>(builder.Configuration.GetSection(DashboardOptions.SectionName));
var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
if (OperatingSystem.IsWindows()
    && string.Equals(storageOptions.DatabasePath, "data/zoomcheck.db", StringComparison.OrdinalIgnoreCase))
{
    storageOptions.DatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZoomCheck",
        "data",
        "zoomcheck.db");
}

builder.Services.AddSingleton(new SqliteAttendanceRepository(storageOptions.DatabasePath));
builder.Services.AddSingleton<ExcelRosterParser>();
builder.Services.AddSingleton<AttendanceMatcher>();
builder.Services.AddSingleton<AttendanceApplicationService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ZoomWebhookValidator>();
builder.Services.AddHttpClient<ZoomOAuthTokenService>();
builder.Services.AddHttpClient<ZoomApiClient>();
builder.Services.AddSingleton<ZoomLiveSyncService>();
builder.Services.AddSingleton<ZoomAppBridgeService>();
builder.Services.AddSingleton<ZoomRecoveryService>();
builder.Services.AddHostedService<ZoomRecoveryBackgroundService>();
builder.Services.AddHostedService<DashboardBrowserLauncher>();
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var zoomAppOptions = builder.Configuration.GetSection(ZoomAppOptions.SectionName).Get<ZoomAppOptions>() ?? new ZoomAppOptions();
if (Uri.TryCreate(zoomAppOptions.HomeUrl, UriKind.Absolute, out var configuredZoomAppHome)
    && configuredZoomAppHome.Scheme == Uri.UriSchemeHttps)
{
    var allowedHosts = (builder.Configuration["AllowedHosts"] ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Append(configuredZoomAppHome.Host)
        .Distinct(StringComparer.OrdinalIgnoreCase);
    builder.Configuration["AllowedHosts"] = string.Join(';', allowedHosts);
}
var zoomAppOrigins = zoomAppOptions.AllowedOrigins
    .Concat(Uri.TryCreate(zoomAppOptions.HomeUrl, UriKind.Absolute, out var zoomAppHome)
        ? new[] { zoomAppHome.GetLeftPart(UriPartial.Authority) }
        : Array.Empty<string>())
    .Where(origin => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (zoomAppOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy("ZoomAppBridge", policy => policy
        .WithOrigins(zoomAppOrigins)
        .WithMethods("POST", "OPTIONS")
        .WithHeaders("Content-Type", "Accept")
        .SetPreflightMaxAge(TimeSpan.FromHours(1))));
}

var app = builder.Build();

await app.Services.GetRequiredService<SqliteAttendanceRepository>().InitializeAsync();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseDefaultFiles();
app.UseMiddleware<ZoomAppSurfaceGuardMiddleware>();
app.UseStaticFiles();
if (zoomAppOrigins.Length > 0)
{
    app.UseCors("ZoomAppBridge");
}
app.UseMiddleware<LocalOriginGuardMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
// ASP.NET Core route matching treats a trailing slash as equivalent here, so
// one endpoint intentionally handles both /zoom-app and /zoom-app/.
app.MapGet("/zoom-app", async context =>
{
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "zoom-app", "index.html"));
});
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html"));
});

app.Run();
