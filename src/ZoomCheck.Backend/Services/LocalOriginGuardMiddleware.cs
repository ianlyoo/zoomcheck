using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class LocalOriginGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _zoomAppOrigins;

    public LocalOriginGuardMiddleware(RequestDelegate next, IOptions<ZoomAppOptions>? zoomAppOptions = null)
    {
        _next = next;
        var configured = zoomAppOptions?.Value ?? new ZoomAppOptions();
        _zoomAppOrigins = configured.AllowedOrigins
            .Concat(Uri.TryCreate(configured.HomeUrl, UriKind.Absolute, out var home)
                ? new[] { home.GetLeftPart(UriPartial.Authority) }
                : Array.Empty<string>())
            .Select(NormalizeOrigin)
            .Where(origin => origin is not null)
            .Select(origin => origin!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var isBridgeMutation = IsMutation(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/zoom-app/bridge");
        if (isBridgeMutation && !context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Zoom App origin is required.",
                Detail = "Bridge mutations must come from the configured HTTPS Zoom App page.",
                Status = StatusCodes.Status403Forbidden
            });
            return;
        }

        if (IsMutation(context.Request.Method)
            && context.Request.Headers.TryGetValue("Origin", out var originHeader)
            && !IsSameOrigin(context.Request, originHeader.ToString())
            && !IsAllowedZoomAppBridgeOrigin(context.Request, originHeader.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Title = "Cross-origin request blocked.",
                Detail = "ZoomCheck accepts browser mutations only from its own local dashboard.",
                Status = StatusCodes.Status403Forbidden
            });
            return;
        }

        await _next(context);
    }

    private static bool IsMutation(string method)
        => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static bool IsSameOrigin(HttpRequest request, string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        return string.Equals(originUri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsAllowedZoomAppBridgeOrigin(HttpRequest request, string origin)
        => request.Path.StartsWithSegments("/api/zoom-app/bridge")
            && NormalizeOrigin(origin) is { } normalized
            && _zoomAppOrigins.Contains(normalized);

    private static string? NormalizeOrigin(string? origin)
        => Uri.TryCreate(origin?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
}
