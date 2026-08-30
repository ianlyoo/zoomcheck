using Microsoft.AspNetCore.Mvc;

namespace ZoomCheck.Backend.Services;

public sealed class LocalOriginGuardMiddleware
{
    private readonly RequestDelegate _next;

    public LocalOriginGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsMutation(context.Request.Method)
            && context.Request.Headers.TryGetValue("Origin", out var originHeader)
            && !IsSameOrigin(context.Request, originHeader.ToString()))
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
}
