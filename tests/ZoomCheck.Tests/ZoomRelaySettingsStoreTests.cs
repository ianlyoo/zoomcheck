using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Tests;

public sealed class ZoomRelaySettingsStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("zoomcheck-relay-settings").FullName;

    [Theory]
    [InlineData("https://relay.example", "https://relay.example/")]
    [InlineData("https://relay.example/zoom-app/", "https://relay.example/")]
    public void NormalizeBaseUrl_AcceptsHttpsOrigin(string input, string expected)
    {
        Assert.True(ZoomRelayOptions.TryNormalizeBaseUrl(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://relay.example")]
    [InlineData("https://user:password@relay.example")]
    [InlineData("https://relay.example/?token=value")]
    public void NormalizeBaseUrl_RejectsUnsafeValues(string input)
        => Assert.False(ZoomRelayOptions.TryNormalizeBaseUrl(input, out _));

    [Fact]
    public async Task SaveAsync_WritesPersistentRelayConfigurationAndAppliesImmediately()
    {
        var path = Path.Combine(_directory, ZoomRelaySettingsStore.FileName);
        var store = new ZoomRelaySettingsStore(path, activeBaseUrl: null);
        var changedCount = 0;
        store.Changed += () => changedCount++;

        var saved = await store.SaveAsync("https://relay.example/zoom-app/", CancellationToken.None);

        Assert.True(saved.Configured);
        Assert.False(saved.RestartRequired);
        Assert.Equal("https://relay.example/", saved.BaseUrl);
        Assert.Equal("https://relay.example/", store.GetActiveBaseUri()?.AbsoluteUri);
        Assert.Equal(1, changedCount);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(json[ZoomRelayOptions.SectionName]![nameof(ZoomRelayOptions.Enabled)]!.GetValue<bool>());
        Assert.Equal(
            "https://relay.example/",
            json[ZoomRelayOptions.SectionName]![nameof(ZoomRelayOptions.BaseUrl)]!.GetValue<string>());
        Assert.Single(json);
    }

    [Fact]
    public async Task SaveAsync_SameActiveOriginDoesNotFireChange()
    {
        var path = Path.Combine(_directory, ZoomRelaySettingsStore.FileName);
        var store = new ZoomRelaySettingsStore(path, "https://relay.example/");
        var changedCount = 0;
        store.Changed += () => changedCount++;

        var saved = await store.SaveAsync("https://relay.example/zoom-app", CancellationToken.None);

        Assert.True(saved.Configured);
        Assert.False(saved.RestartRequired);
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task SaveAsync_EnablesPreviouslyDisabledRelayImmediately()
    {
        var path = Path.Combine(_directory, ZoomRelaySettingsStore.FileName);
        var store = new ZoomRelaySettingsStore(path, "https://relay.example/", activeEnabled: false);
        var changedCount = 0;
        store.Changed += () => changedCount++;

        var saved = await store.SaveAsync("https://relay.example/", CancellationToken.None);

        Assert.True(saved.Configured);
        Assert.False(saved.RestartRequired);
        Assert.Equal(saved, store.Get());
        Assert.Equal("https://relay.example/", store.GetActiveBaseUri()?.AbsoluteUri);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public async Task SaveAsync_AppliesNewUrlImmediatelyAndPersistsAcrossRestart()
    {
        var path = Path.Combine(_directory, ZoomRelaySettingsStore.FileName);
        var store = new ZoomRelaySettingsStore(path, "https://old.example/");
        await store.SaveAsync("https://new.example/zoom-app/", CancellationToken.None);

        Assert.Equal((true, "https://new.example/", false), store.Get());
        Assert.Equal("https://new.example/", store.GetActiveBaseUri()?.AbsoluteUri);

        var configuration = LegacyConfiguration();
        ZoomRelaySettingsStore.AddUserConfiguration(configuration, path);
        var restarted = new ZoomRelaySettingsStore(path,
            configuration["ZoomRelay:BaseUrl"], configuration.GetValue<bool>("ZoomRelay:Enabled"));
        Assert.Equal((true, "https://new.example/", false), restarted.Get());
    }

    [Fact]
    public async Task SavedSettings_OverrideEmergencyRelayEnvironmentWithoutOverridingOtherKeys()
    {
        var path = Path.Combine(_directory, ZoomRelaySettingsStore.FileName);
        await File.WriteAllTextAsync(path, """
            {"ZoomRelay":{"BaseUrl":"https://saved.example/zoom-app/","Enabled":true,"PollIntervalMilliseconds":9999},
             "Storage":{"DatabasePath":"must-not-override.db"},"Zoom":{"ClientSecret":"must-not-override"}}
            """);
        var configuration = LegacyConfiguration();

        ZoomRelaySettingsStore.AddUserConfiguration(configuration, path);

        Assert.Equal("https://saved.example/", configuration["ZoomRelay:BaseUrl"]);
        Assert.True(configuration.GetValue<bool>("ZoomRelay:Enabled"));
        Assert.Equal("1500", configuration["ZoomRelay:PollIntervalMilliseconds"]);
        Assert.Equal("existing.db", configuration["Storage:DatabasePath"]);
        Assert.Equal("existing-secret", configuration["Zoom:ClientSecret"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("{\"ZoomRelay\":{\"BaseUrl\":\"http://insecure.example/\"}}")]
    public async Task MissingOrInvalidSavedSettings_PreserveLegacyConfiguration(string? json)
    {
        var path = Path.Combine(_directory, ZoomRelaySettingsStore.FileName);
        if (json is not null) { await File.WriteAllTextAsync(path, json); }
        var configuration = LegacyConfiguration();

        ZoomRelaySettingsStore.AddUserConfiguration(configuration, path);

        Assert.Equal("https://legacy.example/", configuration["ZoomRelay:BaseUrl"]);
        Assert.False(configuration.GetValue<bool>("ZoomRelay:Enabled"));
    }

    [Fact]
    public async Task SaveAsync_PreservesOtherSettingsAndInvalidSaveDoesNotReplacePendingUrl()
    {
        var path = Path.Combine(_directory, ZoomRelaySettingsStore.FileName);
        await File.WriteAllTextAsync(path, """
            {"ZoomRelay":{"PollIntervalMilliseconds":2000},"Other":{"Value":"keep"}}
            """);
        var store = new ZoomRelaySettingsStore(path, activeBaseUrl: null);
        await store.SaveAsync("https://saved.example/", CancellationToken.None);
        var before = await File.ReadAllTextAsync(path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("http://invalid.example/", CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
        var json = JsonNode.Parse(before)!;
        Assert.Equal(2000, json["ZoomRelay"]!["PollIntervalMilliseconds"]!.GetValue<int>());
        Assert.Equal("keep", json["Other"]!["Value"]!.GetValue<string>());
        Assert.Equal((true, "https://saved.example/", false), store.Get());
    }

    private static ConfigurationManager LegacyConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ZoomRelay:BaseUrl"] = "https://legacy.example/",
            ["ZoomRelay:Enabled"] = "false",
            ["ZoomRelay:PollIntervalMilliseconds"] = "1500",
            ["Storage:DatabasePath"] = "existing.db",
            ["Zoom:ClientSecret"] = "existing-secret"
        });
        return configuration;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
