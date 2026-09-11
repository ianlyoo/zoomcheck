namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomRelaySettingsResponse(
    bool Configured,
    string BaseUrl,
    bool RestartRequired);

public sealed record UpdateZoomRelaySettingsRequest(string? BaseUrl);
