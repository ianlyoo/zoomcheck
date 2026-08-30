using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomOAuthTokenService
{
    private readonly HttpClient _httpClient;
    private readonly ZoomOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset? _expiresAt;

    public ZoomOAuthTokenService(HttpClient httpClient, IOptions<ZoomOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public bool IsConfigured =>
        HasConfiguredValue(_options.ClientId)
        && HasConfiguredValue(_options.ClientSecret)
        && HasConfiguredValue(_options.AccountId);

    public DateTimeOffset? ExpiresAt => _expiresAt;

    public bool HasUsableCachedToken =>
        !string.IsNullOrWhiteSpace(_accessToken)
        && _expiresAt is not null
        && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1);

    public async Task<string?> TryGetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_accessToken) && _expiresAt is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _accessToken;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && _expiresAt is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _accessToken;
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "https://zoom.us/oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "account_credentials",
                    ["account_id"] = _options.AccountId
                })
            };

            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new ZoomOAuthException(response.StatusCode, responseBody);
            }

            var payload = await response.Content.ReadFromJsonAsync<ZoomOAuthTokenResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Zoom OAuth token response was empty.");

            _accessToken = payload.AccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class ZoomOAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    private static bool HasConfiguredValue(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("replace-with-your-", StringComparison.OrdinalIgnoreCase);
}

public sealed class ZoomOAuthException : Exception
{
    public ZoomOAuthException(System.Net.HttpStatusCode statusCode, string responseBody)
        : base($"Zoom OAuth token request returned HTTP {(int)statusCode}.")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public System.Net.HttpStatusCode StatusCode { get; }

    public string ResponseBody { get; }

    public string UserMessage =>
        $"Zoom returned HTTP {(int)StatusCode}. Verify Account ID, Client ID, Client Secret, and that the Server-to-Server OAuth app is activated.";
}
