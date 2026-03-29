namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomSettingsStatusResponse(
    bool WebhookSecretConfigured,
    bool OAuthConfigured,
    bool TokenAvailable,
    DateTimeOffset? TokenExpiresAt,
    string[] MissingFields);
