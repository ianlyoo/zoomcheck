using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomApiClient
{
    private const string BaseUrl = "https://api.zoom.us/v2";

    private readonly HttpClient _httpClient;
    private readonly ZoomOAuthTokenService _tokenService;
    private readonly ZoomRecoveryOptions _recoveryOptions;
    private readonly JsonSerializerOptions _jsonOptions;

    public ZoomApiClient(HttpClient httpClient, ZoomOAuthTokenService tokenService, Microsoft.Extensions.Options.IOptions<ZoomRecoveryOptions> recoveryOptions)
    {
        _httpClient = httpClient;
        _tokenService = tokenService;
        _recoveryOptions = recoveryOptions.Value;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task<IReadOnlyList<ZoomMeeting>> GetLiveMeetingsForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var pageSize = ClampPageSize(_recoveryOptions.MeetingsPageSize);
        var result = new List<ZoomMeeting>();

        for (var pageNumber = 1;; pageNumber++)
        {
            var query = $"/users/{Uri.EscapeDataString(userId)}/meetings?type=live&page_size={pageSize}&page_number={pageNumber}";
            using var response = await SendGetRequestAsync(query, cancellationToken);
            var payload = await ReadResponseAsync<ZoomMeetingsResponse>(response, cancellationToken);

            if (payload?.Meetings is not null)
            {
                result.AddRange(payload.Meetings);
            }

            var pageCount = payload?.PageCount ?? 1;
            if (pageCount <= pageNumber)
            {
                break;
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<ZoomMeetingParticipant>> GetLiveParticipantsAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var pageSize = ClampPageSize(_recoveryOptions.ParticipantsPageSize);
        var nextToken = string.Empty;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ZoomMeetingParticipant>();

        for (var page = 1; page <= Math.Clamp(_recoveryOptions.MaxParticipantPages, 1, 100); page++)
        {
            var query = $"/metrics/meetings/{Uri.EscapeDataString(meetingId)}/participants?type=live&page_size={pageSize}";
            if (!string.IsNullOrWhiteSpace(nextToken))
            {
                query += $"&next_page_token={Uri.EscapeDataString(nextToken)}";
            }

            using var response = await SendGetRequestAsync(query, cancellationToken);
            var payload = await ReadResponseAsync<ZoomParticipantsResponse>(response, cancellationToken);

            if (payload?.Participants is not null)
            {
                result.AddRange(payload.Participants);
            }

            nextToken = payload?.NextPageToken ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nextToken))
            {
                return result;
            }

            if (!seenTokens.Add(nextToken))
            {
                throw new ZoomApiException(
                    HttpStatusCode.BadGateway,
                    "Zoom returned a repeated next_page_token.",
                    query);
            }
        }

        throw new ZoomApiException(
            HttpStatusCode.BadGateway,
            $"Zoom participant pagination exceeded the configured limit of {_recoveryOptions.MaxParticipantPages} pages.",
            $"/metrics/meetings/{Uri.EscapeDataString(meetingId)}/participants");
    }

    public async Task<IReadOnlyList<ZoomUser>> GetActiveUsersAsync(CancellationToken cancellationToken = default)
    {
        var pageSize = ClampPageSize(_recoveryOptions.UsersPageSize);
        var result = new List<ZoomUser>();

        for (var pageNumber = 1;; pageNumber++)
        {
            var query = $"/users?status=active&page_size={pageSize}&page_number={pageNumber}";
            using var response = await SendGetRequestAsync(query, cancellationToken);
            var payload = await ReadResponseAsync<ZoomUsersResponse>(response, cancellationToken);

            if (payload?.Users is not null)
            {
                result.AddRange(payload.Users);
            }

            var pageCount = payload?.PageCount ?? 1;
            if (pageCount <= pageNumber)
            {
                break;
            }
        }

        return result;
    }

    private async Task<HttpResponseMessage> SendGetRequestAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        var token = await _tokenService.TryGetAccessTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ZoomApiException(HttpStatusCode.Unauthorized, "Zoom OAuth token is not configured or could not be acquired.", pathAndQuery);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            && response.Headers.RetryAfter?.Delta is { } retryAfter
            && retryAfter > TimeSpan.Zero
            && retryAfter <= TimeSpan.FromSeconds(10))
        {
            response.Dispose();
            await Task.Delay(retryAfter, cancellationToken);
            using var retryRequest = new HttpRequestMessage(HttpMethod.Get, BaseUrl + pathAndQuery);
            retryRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            response = await _httpClient.SendAsync(retryRequest, cancellationToken);
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var error = new ZoomApiException(response.StatusCode, body, pathAndQuery);
            response.Dispose();
            throw error;
        }

        return response;
    }

    private async Task<T?> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken);
    }

    private static int ClampPageSize(int pageSize)
        => Math.Clamp(pageSize, 1, 300);
}

public sealed class ZoomApiException : Exception
{
    public ZoomApiException(HttpStatusCode statusCode, string body, string endpoint)
        : base($"Zoom API request failed for '{endpoint}' with {(int)statusCode} {statusCode}.")
    {
        StatusCode = statusCode;
        Body = body;
        Endpoint = endpoint;
    }

    public HttpStatusCode StatusCode { get; }

    public string Body { get; }

    public string Endpoint { get; }
}

public sealed class ZoomMeetingsResponse
{
    [JsonPropertyName("page_size")]
    public int? PageSize { get; set; }

    [JsonPropertyName("page_count")]
    public int? PageCount { get; set; }

    [JsonPropertyName("page_number")]
    public int? PageNumber { get; set; }

    [JsonPropertyName("total_records")]
    public int? TotalRecords { get; set; }

    [JsonPropertyName("meetings")]
    public List<ZoomMeeting>? Meetings { get; set; }
}

public sealed class ZoomMeeting
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("host_id")]
    public string? HostId { get; set; }

    [JsonPropertyName("topic")]
    public string? Topic { get; set; }

    [JsonPropertyName("type")]
    public int? Type { get; set; }

    [JsonPropertyName("start_time")]
    public string? StartTime { get; set; }
}

public sealed class ZoomParticipantsResponse
{
    [JsonPropertyName("next_page_token")]
    public string? NextPageToken { get; set; }

    [JsonPropertyName("participants")]
    public List<ZoomMeetingParticipant>? Participants { get; set; }
}

public sealed class ZoomMeetingParticipant
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("user_name")]
    public string? UserName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("user_email")]
    public string? UserEmail { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("join_time")]
    public string? JoinTime { get; set; }

    [JsonPropertyName("leave_time")]
    public string? LeaveTime { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonIgnore]
    public string? EffectiveName => string.IsNullOrWhiteSpace(UserName) ? Name : UserName;

    [JsonIgnore]
    public string? EffectiveEmail => string.IsNullOrWhiteSpace(UserEmail) ? Email : UserEmail;
}

public sealed class ZoomUsersResponse
{
    [JsonPropertyName("page_size")]
    public int? PageSize { get; set; }

    [JsonPropertyName("page_count")]
    public int? PageCount { get; set; }

    [JsonPropertyName("page_number")]
    public int? PageNumber { get; set; }

    [JsonPropertyName("total_records")]
    public int? TotalRecords { get; set; }

    [JsonPropertyName("users")]
    public List<ZoomUser>? Users { get; set; }
}

public sealed class ZoomUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}
