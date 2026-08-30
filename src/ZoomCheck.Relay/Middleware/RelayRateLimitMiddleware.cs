using ZoomCheck.Relay.Services;

namespace ZoomCheck.Relay.Middleware;

/// <summary>
/// Applies the per-client request budget to every relay API call. The health endpoint is exempt
/// so platform probes cannot be starved by client traffic.
/// </summary>
public sealed class RelayRateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RelayRateLimiter _limiter;

    public RelayRateLimitMiddleware(RequestDelegate next, RelayRateLimiter limiter)
    {
        _next = next;
        _limiter = limiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!_limiter.TryAcquire(key))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsJsonAsync(new { error = "rate_limited" });
            return;
        }

        await _next(context);
    }
}
