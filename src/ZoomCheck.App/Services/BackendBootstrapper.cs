using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ZoomCheck.App.Models;

namespace ZoomCheck.App.Services;

public sealed class BackendBootstrapper : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly AppRuntimeOptions _options;
    private Process? _ownedBackendProcess;

    public BackendBootstrapper(HttpClient httpClient, AppRuntimeOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<BackendBootstrapResult> EnsureBackendAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (await IsHealthyAsync(cancellationToken))
        {
            return new BackendBootstrapResult(true, false, "ZoomCheck is ready.");
        }

        AppPaths.EnsureUserDirectories();
        var backendPath = AppPaths.GetBackendExecutablePath(_options.BackendRelativeExecutablePath);
        if (!File.Exists(backendPath))
        {
            return new BackendBootstrapResult(false, false, "ZoomCheck backend files were not found. Reinstall ZoomCheck with the full Windows package.");
        }

        try
        {
            _ownedBackendProcess = StartBackendProcess(backendPath);
        }
        catch (Exception ex)
        {
            return new BackendBootstrapResult(false, false, $"ZoomCheck could not start its local service: {ex.Message}");
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(_options.StartupTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_ownedBackendProcess?.HasExited == true)
            {
                return new BackendBootstrapResult(false, true, "ZoomCheck local service stopped during startup. Please reinstall ZoomCheck or contact support.");
            }

            if (await IsHealthyAsync(cancellationToken))
            {
                return new BackendBootstrapResult(true, true, "ZoomCheck local service started successfully.");
            }

            await Task.Delay(_options.PollDelayMilliseconds, cancellationToken);
        }

        return new BackendBootstrapResult(false, true, "ZoomCheck local service did not become ready in time. Close ZoomCheck and try again.");
    }

    private Process StartBackendProcess(string backendPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = backendPath,
            WorkingDirectory = Path.GetDirectoryName(backendPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.Environment["ASPNETCORE_URLS"] = _options.BackendBaseUrl.TrimEnd('/');
        startInfo.Environment["Storage__DatabasePath"] = AppPaths.GetDatabasePath();
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Backend process did not start.");
    }

    private async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(_options.HealthPath, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownedBackendProcess is null || _ownedBackendProcess.HasExited)
        {
            return;
        }

        try
        {
            _ownedBackendProcess.Kill(entireProcessTree: true);
            await _ownedBackendProcess.WaitForExitAsync();
        }
        catch
        {
        }
        finally
        {
            _ownedBackendProcess.Dispose();
            _ownedBackendProcess = null;
        }
    }
}
