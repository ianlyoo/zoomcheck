using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;
using System.Net;

namespace ZoomCheck.Backend.Services;

/// <summary>
/// Keeps the local dashboard private when a narrow HTTPS tunnel is used as a
/// Zoom App Home URL. The public host may serve only the companion assets and
/// its authenticated pairing bridge; all dashboard and attendance APIs remain
/// available only through localhost.
/// </summary>
public sealed class ZoomAppSurfaceGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _isConfigured;

    public ZoomAppSurfaceGuardMiddleware(RequestDelegate next, IOptions<ZoomAppOptions> options)
    {
        _next = next;
        _isConfigured = Uri.TryCreate(options.Value.HomeUrl?.Trim(), UriKind.Absolute, out var home)
            && home.Scheme == Uri.UriSchemeHttps;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_isConfigured
            || IsLoopbackHost(context.Request.Host.Host)
            || IsPublicZoomAppPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "Not found.",
            Detail = "The public Zoom App address exposes only the companion bridge. Open the dashboard through http://127.0.0.1:5078.",
            Status = StatusCodes.Status404NotFound
        });
    }

    private static bool IsPublicZoomAppPath(PathString path)
        => path.StartsWithSegments("/zoom-app")
            || path.StartsWithSegments("/api/zoom-app/bridge");

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
