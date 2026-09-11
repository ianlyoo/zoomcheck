using System.Text.Json;
using System.Text.Json.Nodes;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomRelaySettingsStore
{
    public const string FileName = "appsettings.User.json";
    private readonly string _path;
    private readonly string _activeBaseUrl;
    private readonly bool _activeEnabled;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private SavedRelaySettings? _saved;

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
        _saved = ReadSavedSettings(path);
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
                _saved?.BaseUrl ?? _activeBaseUrl,
                _saved is { } saved && (saved.Enabled != _activeEnabled
                    || !string.Equals(_activeBaseUrl, saved.BaseUrl, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public async Task<(bool Configured, string BaseUrl, bool RestartRequired)> SaveAsync(
        string? baseUrl,
        CancellationToken cancellationToken)
    {
        if (!ZoomRelayOptions.TryNormalizeBaseUrl(baseUrl, out var normalized))
        {
            throw new ArgumentException("A clean HTTPS relay root URL is required.", nameof(baseUrl));
        }

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
                _saved = new SavedRelaySettings(normalized, true);
            }
            return Get();
        }
        finally
        {
            _gate.Release();
        }
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
                || baseUrl.ValueKind != JsonValueKind.String
                || !ZoomRelayOptions.TryNormalizeBaseUrl(baseUrl.GetString(), out var normalized))
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
