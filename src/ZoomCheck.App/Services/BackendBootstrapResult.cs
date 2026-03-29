namespace ZoomCheck.App.Services;

public sealed record BackendBootstrapResult(bool Success, bool StartedByApp, string Message);
