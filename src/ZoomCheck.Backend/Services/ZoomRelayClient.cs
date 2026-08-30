using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;
using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomRelayClient
{
    private readonly HttpClient _http;
    private readonly ZoomRelayOptions _options;

    public ZoomRelayClient(HttpClient http, IOptions<ZoomRelayOptions> options)
    {
        _http = http;
        _options = options.Value;
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 3, 60));
    }

    public bool IsConfigured => _options.Enabled && BaseUri() is not null;

    public Uri? CompanionUrl()
        => BaseUri() is { } baseUri ? new Uri(baseUri, "zoom-app/") : null;

    public async Task<CreatePairingResponse> RegisterAsync(
        CreatePairingRequest request,
        CancellationToken cancellationToken)
        => await SendAsync<CreatePairingResponse>(
            HttpMethod.Post, "api/v1/pairings", null, request, cancellationToken);

    public async Task<RelayMessagesResponse> PollDesktopAsync(
        string sessionId,
        string token,
        long afterSequence,
        CancellationToken cancellationToken)
        => await SendAsync<RelayMessagesResponse>(
            HttpMethod.Get,
            $"api/v1/sessions/{Uri.EscapeDataString(sessionId)}/desktop/messages?afterSequence={afterSequence}",
            token,
            null,
            cancellationToken);

    public async Task SendDesktopAsync(
        string sessionId,
        string token,
        RelayEnvelope envelope,
        CancellationToken cancellationToken)
        => _ = await SendAsync<RelayAcceptedResponse>(
            HttpMethod.Post,
            $"api/v1/sessions/{Uri.EscapeDataString(sessionId)}/desktop/messages",
            token,
            envelope,
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativePath,
        string? bearerToken,
        object? body,
        CancellationToken cancellationToken)
    {
        var baseUri = BaseUri()
            ?? throw new ZoomRelayException("The ZoomCheck relay URL is not configured.");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ZoomRelayException(
                response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Gone
                    ? "The ZoomCheck relay session expired. Create a new pairing code."
                    : $"The ZoomCheck relay returned HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new ZoomRelayException("The ZoomCheck relay returned an empty response.");
    }

    private Uri? BaseUri()
    {
        if (!Uri.TryCreate(_options.BaseUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var value = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
        return new Uri(value);
    }
}

public sealed class ZoomRelayException : Exception
{
    public ZoomRelayException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
