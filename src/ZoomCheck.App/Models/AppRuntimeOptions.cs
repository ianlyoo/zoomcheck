using System.IO;

namespace ZoomCheck.App.Models;

public sealed class AppRuntimeOptions
{
    public string BackendBaseUrl { get; init; } = "http://127.0.0.1:5078/";

    public string BackendRelativeExecutablePath { get; init; } = Path.Combine("Backend", "ZoomCheck.Backend.exe");

    public string HealthPath { get; init; } = "health";

    public int StartupTimeoutSeconds { get; init; } = 20;

    public int PollDelayMilliseconds { get; init; } = 500;
}
