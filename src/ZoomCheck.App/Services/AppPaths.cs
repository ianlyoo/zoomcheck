using System;
using System.IO;

namespace ZoomCheck.App.Services;

public static class AppPaths
{
    public static string GetAppRoot() => AppContext.BaseDirectory;

    public static string GetBackendExecutablePath(string relativePath)
        => Path.GetFullPath(Path.Combine(GetAppRoot(), relativePath));

    public static string GetUserDataRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomCheck");

    public static string GetDatabasePath()
        => Path.Combine(GetUserDataRoot(), "data", "zoomcheck.db");

    public static string GetLogsRoot()
        => Path.Combine(GetUserDataRoot(), "logs");

    public static void EnsureUserDirectories()
    {
        Directory.CreateDirectory(Path.Combine(GetUserDataRoot(), "data"));
        Directory.CreateDirectory(GetLogsRoot());
    }
}
