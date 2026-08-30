namespace ZoomCheck.Relay.Middleware;

/// <summary>
/// Baseline response hardening. The companion is served inside a Zoom webview, so framing is
/// controlled with a CSP <c>frame-ancestors</c> directive rather than a blanket DENY.
/// </summary>
public sealed class RelaySecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public RelaySecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
        headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' https://appssdk.zoom.us; style-src 'self'; " +
            "connect-src 'self'; " +
            "img-src 'self' data:; object-src 'none'; base-uri 'none'; " +
            "frame-ancestors https://zoom.us https://*.zoom.us";
        return _next(context);
    }
}
