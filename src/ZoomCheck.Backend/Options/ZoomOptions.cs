namespace ZoomCheck.Backend.Options;

public sealed class ZoomOptions
{
    public const string SectionName = "Zoom";

    public string WebhookSecretToken { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public int RequestTimestampToleranceSeconds { get; set; } = 300;
}
