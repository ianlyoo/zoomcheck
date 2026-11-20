using System.Text.Json;
using System.Text.Json.Nodes;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomRelaySettingsStore
{
    public const string FileName = "appsettings.User.json";
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private string _activeBaseUrl;
    private bool _activeEnabled;

    public event Action? Changed;

    public ZoomRelaySettingsStore(IHostEnvironment environment, IConfiguration configuration)
        : this(
            UserSettingsPath(environment),
            configuration[$"{ZoomRelayOptions.SectionName}:BaseUrl"],
            configuration.GetValue($"{ZoomRelayOptions.SectionName}:Enabled", true))
    {
    }

    internal ZoomRelaySettingsStore(string path, string? activeBaseUrl, bool activeEnabled = true)
    {
        _path = path;
        _activeBaseUrl = ZoomRelayOptions.TryNormalizeBaseUrl(
            activeBaseUrl, out var value)
            ? value
            : string.Empty;
        _activeEnabled = activeEnabled;
    }

    // An explicit choice in this PC's settings wins over the emergency environment
    // workaround. Only these two relay keys override the normal configuration chain.
    public static void AddUserConfiguration(IConfigurationBuilder configuration, string path)
    {
        if (ReadSavedSettings(path) is not { } saved)
        {
            return;
        }

        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{ZoomRelayOptions.SectionName}:BaseUrl"] = saved.BaseUrl,
            [$"{ZoomRelayOptions.SectionName}:Enabled"] = saved.Enabled.ToString()
        });
    }

    public static string UserSettingsPath(IHostEnvironment environment)
    {
        var root = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZoomCheck", "config")
            : Path.Combine(environment.ContentRootPath, "data", "config");
        return Path.Combine(root, FileName);
    }

    public (bool Configured, string BaseUrl, bool RestartRequired) Get()
    {
        lock (_stateLock)
        {
            return (_activeEnabled && !string.IsNullOrEmpty(_activeBaseUrl),
                _activeBaseUrl,
                false);
        }
    }

    public Uri? GetActiveBaseUri()
    {
        lock (_stateLock)
        {
            return _activeEnabled && !string.IsNullOrEmpty(_activeBaseUrl)
                ? new Uri(_activeBaseUrl)
                : null;
        }
    }

    public async Task<(bool Configured, string BaseUrl, bool RestartRequired)> SaveAsync(
        string? baseUrl,
        CancellationToken cancellationToken)
    {
        var clearing = string.IsNullOrWhiteSpace(baseUrl);
        var normalized = string.Empty;
        if (!clearing && !ZoomRelayOptions.TryNormalizeBaseUrl(baseUrl, out normalized))
        {
            throw new ArgumentException("A clean HTTPS relay root URL is required.", nameof(baseUrl));
        }

        bool changed;
        (bool Configured, string BaseUrl, bool RestartRequired) snapshot;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            JsonObject root;
            if (File.Exists(_path))
            {
                await using var existing = File.OpenRead(_path);
                root = await JsonNode.ParseAsync(existing, cancellationToken: cancellationToken) as JsonObject
                    ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var relay = root[ZoomRelayOptions.SectionName] as JsonObject ?? new JsonObject();
            if (relay.Parent is null) { root[ZoomRelayOptions.SectionName] = relay; }
            relay[nameof(ZoomRelayOptions.Enabled)] = true;
            relay[nameof(ZoomRelayOptions.BaseUrl)] = normalized;
            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporaryPath, _path, overwrite: true);
            lock (_stateLock)
            {
                var wasConfigured = _activeEnabled && !string.IsNullOrEmpty(_activeBaseUrl);
                var nowConfigured = !clearing;
                changed = wasConfigured != nowConfigured
                    || !string.Equals(_activeBaseUrl, normalized, StringComparison.OrdinalIgnoreCase);
                _activeBaseUrl = normalized;
                _activeEnabled = true;
                snapshot = (nowConfigured, _activeBaseUrl, false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return snapshot;
    }

    private static SavedRelaySettings? ReadSavedSettings(string path)
    {
        if (!File.Exists(path)) { return null; }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(ZoomRelayOptions.SectionName, out var relay)
                || relay.ValueKind != JsonValueKind.Object
                || !relay.TryGetProperty(nameof(ZoomRelayOptions.BaseUrl), out var baseUrl)
                || baseUrl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var rawBaseUrl = baseUrl.GetString();
            string normalized;
            if (string.IsNullOrWhiteSpace(rawBaseUrl))
            {
                normalized = string.Empty;
            }
            else if (!ZoomRelayOptions.TryNormalizeBaseUrl(rawBaseUrl, out normalized))
            {
                return null;
            }

            var enabled = !relay.TryGetProperty(nameof(ZoomRelayOptions.Enabled), out var value)
                || value.ValueKind != JsonValueKind.False;
            return new SavedRelaySettings(normalized, enabled);
        }
        catch (JsonException)
        {
            // A damaged settings file must not disable a working legacy configuration.
            return null;
        }
    }

    private sealed record SavedRelaySettings(string BaseUrl, bool Enabled);
}
