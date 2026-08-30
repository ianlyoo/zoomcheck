using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Tests;

public sealed class ZoomAppSurfaceGuardMiddlewareTests
{
    private static readonly IOptions<ZoomAppOptions> Options = Microsoft.Extensions.Options.Options.Create(
        new ZoomAppOptions { HomeUrl = "https://zoomcheck.example/zoom-app/" });

    [Theory]
    [InlineData("/zoom-app/index.html")]
    [InlineData("/api/zoom-app/bridge/connect")]
    public async Task PublicHost_AllowsOnlyCompanionSurface(string path)
    {
        var reachedNext = false;
        var middleware = new ZoomAppSurfaceGuardMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        }, Options);
        var context = Context("zoomcheck.example", path);

        await middleware.InvokeAsync(context);

        Assert.True(reachedNext);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/roster")]
    [InlineData("/api/zoom/connection-status")]
    public async Task PublicHost_BlocksDashboardAndOtherApis(string path)
    {
        var reachedNext = false;
        var middleware = new ZoomAppSurfaceGuardMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        }, Options);
        var context = Context("zoomcheck.example", path);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(reachedNext);
    }

    [Fact]
    public async Task Localhost_AllowsDashboard()
    {
        var reachedNext = false;
        var middleware = new ZoomAppSurfaceGuardMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        }, Options);
        var context = Context("127.0.0.1:5078", "/");

        await middleware.InvokeAsync(context);

        Assert.True(reachedNext);
    }

    private static DefaultHttpContext Context(string host, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
