namespace ZoomCheck.Backend.Contracts;

public sealed record ZoomRelaySettingsResponse(
    bool Configured,
    string BaseUrl,
    bool RestartRequired);

public sealed record UpdateZoomRelaySettingsRequest(string? BaseUrl);

public sealed record CheckZoomRelayHealthRequest(string? BaseUrl);

public sealed record ZoomRelayHealthResponse(
    bool Ok,
    string BaseUrl,
    int? StatusCode,
    long LatencyMs,
    string? ErrorCode,
    string Message,
    int? ActiveSessions = null,
    int? MaxSessions = null);
