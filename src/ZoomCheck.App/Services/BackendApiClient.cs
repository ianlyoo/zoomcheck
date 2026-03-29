using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ZoomCheck.Core.Models;

namespace ZoomCheck.App.Services;

public sealed class BackendApiClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BackendApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task ImportRosterAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/roster/import", new { filePath }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<RosterPerson>> GetRosterAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("api/roster", cancellationToken);
        response.EnsureSuccessStatusCode();
        var roster = await response.Content.ReadFromJsonAsync<IReadOnlyList<RosterPerson>>(_jsonOptions, cancellationToken);
        return roster ?? Array.Empty<RosterPerson>();
    }

    public async Task<AttendanceBoard> GetBoardAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/meetings/{Uri.EscapeDataString(meetingId)}/board", cancellationToken);
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<AttendanceBoard>(_jsonOptions, cancellationToken);
        return board ?? throw new InvalidOperationException("Board response was empty.");
    }

    public async Task SeedDemoAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync($"api/meetings/{Uri.EscapeDataString(meetingId)}/seed-demo", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SaveAliasAsync(string alias, string rosterPersonId, string note, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/roster/alias", new { alias, rosterPersonId, note }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> ExportCsvAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/meetings/{Uri.EscapeDataString(meetingId)}/export", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("health", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<ZoomRecoveryRunResponse> RunRecoveryAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("api/zoom/recovery/run", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ZoomRecoveryRunResponse>(_jsonOptions, cancellationToken);
        return result ?? new ZoomRecoveryRunResponse(false, 0, 0, 0, Array.Empty<ZoomRecoveredMeetingResponse>(), Array.Empty<string>(), "Recovery response was empty.");
    }

    public async Task<ZoomSettingsStatusResponse> GetZoomSettingsStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync("api/zoom/settings-status", cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ZoomSettingsStatusResponse>(_jsonOptions, cancellationToken);
        return result ?? new ZoomSettingsStatusResponse(false, false, false, null, Array.Empty<string>());
    }

    public string BackendBaseUrl => _httpClient.BaseAddress?.ToString() ?? string.Empty;
}

public sealed record ZoomRecoveryRunResponse(
    bool Executed,
    int UsersDiscovered,
    int MeetingsDiscovered,
    int AddedParticipants,
    IReadOnlyList<ZoomRecoveredMeetingResponse> Meetings,
    IReadOnlyList<string> Warnings,
    string? Error);

public sealed record ZoomRecoveredMeetingResponse(
    string MeetingId,
    int DiscoveredParticipants,
    int AddedEvents);

public sealed record ZoomSettingsStatusResponse(
    bool WebhookSecretConfigured,
    bool OAuthConfigured,
    bool TokenAvailable,
    DateTimeOffset? TokenExpiresAt,
    IReadOnlyList<string> MissingFields);
