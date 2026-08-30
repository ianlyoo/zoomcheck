using Microsoft.AspNetCore.Http;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Tests;

public sealed class LocalOriginGuardMiddlewareTests
{
    [Fact]
    public async Task Mutation_FromForeignOrigin_IsRejected()
    {
        var reachedNext = false;
        var middleware = new LocalOriginGuardMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });
        var context = Context("POST", "https://example.test");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(reachedNext);
    }

    [Fact]
    public async Task Mutation_FromDashboardOrigin_IsAllowed()
    {
        var reachedNext = false;
        var middleware = new LocalOriginGuardMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        });
        var context = Context("POST", "http://127.0.0.1:5078");

        await middleware.InvokeAsync(context);

        Assert.True(reachedNext);
    }

    private static DefaultHttpContext Context(string method, string origin)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1", 5078);
        context.Request.Headers.Origin = origin;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
