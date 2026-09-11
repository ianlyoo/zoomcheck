namespace ZoomCheck.Backend.Options;

public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    public bool OpenBrowserOnStart { get; set; } = true;

    public string Url { get; set; } = "http://127.0.0.1:5078";
}
