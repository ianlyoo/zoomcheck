namespace ZoomCheck.Backend.Options;

public sealed class ZoomRelayOptions
{
    public const string SectionName = "ZoomRelay";

    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = string.Empty;

    public int PollIntervalMilliseconds { get; set; } = 1500;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int OfflineSeconds { get; set; } = 20;

    public static bool TryNormalizeBaseUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrWhiteSpace(uri.UserInfo)
            || !string.IsNullOrWhiteSpace(uri.Query)
            || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/";
        return true;
    }
}
