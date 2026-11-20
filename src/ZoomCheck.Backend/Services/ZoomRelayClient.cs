using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;
using ZoomCheck.Relay.Contracts;

namespace ZoomCheck.Backend.Services;

public sealed class ZoomRelayClient
{
    private readonly HttpClient _http;
    private readonly ZoomRelaySettingsStore _settings;

    public ZoomRelayClient(
        HttpClient http,
        IOptions<ZoomRelayOptions> options,
        ZoomRelaySettingsStore settings)
    {
        _http = http;
        _settings = settings;
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.RequestTimeoutSeconds, 3, 60));
    }

    public bool IsConfigured => BaseUri() is not null;

    public Uri? CompanionUrl()
        => BaseUri() is { } baseUri ? new Uri(baseUri, "zoom-app/") : null;

    public async Task<ZoomRelayHealthResponse> CheckHealthAsync(
        string? candidateBaseUrl,
        CancellationToken cancellationToken)
    {
        Uri? baseUri;
        if (string.IsNullOrWhiteSpace(candidateBaseUrl))
        {
            baseUri = BaseUri();
            if (baseUri is null)
            {
                return new ZoomRelayHealthResponse(
                    false,
                    string.Empty,
                    null,
                    0,
                    "not_configured",
                    "Relay URL is not configured.");
            }
        }
        else if (ZoomRelayOptions.TryNormalizeBaseUrl(candidateBaseUrl, out var normalized))
        {
            baseUri = new Uri(normalized);
        }
        else
        {
            return new ZoomRelayHealthResponse(
                false,
                candidateBaseUrl.Trim(),
                null,
                0,
                "invalid_url",
                "A clean HTTPS relay root URL is required.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "health"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return new ZoomRelayHealthResponse(
                    false,
                    baseUri.AbsoluteUri,
                    statusCode,
                    stopwatch.ElapsedMilliseconds,
                    "http_error",
                    $"Relay /health returned HTTP {statusCode}.");
            }

            try
            {
                var health = await response.Content.ReadFromJsonAsync<RelayHealthResponse>(
                    cancellationToken: cancellationToken);
                if (health is null
                    || !string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return new ZoomRelayHealthResponse(
                        false,
                        baseUri.AbsoluteUri,
                        statusCode,
                        stopwatch.ElapsedMilliseconds,
                        "invalid_response",
                        "Endpoint responded, but did not return a valid ZoomCheck relay /health payload.");
                }

                return new ZoomRelayHealthResponse(
                    true,
                    baseUri.AbsoluteUri,
                    statusCode,
                    stopwatch.ElapsedMilliseconds,
                    null,
                    "Relay /health responded OK.",
                    health.ActiveSessions,
                    health.MaxSessions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return new ZoomRelayHealthResponse(
                    false,
                    baseUri.AbsoluteUri,
                    statusCode,
                    stopwatch.ElapsedMilliseconds,
                    "invalid_response",
                    "Endpoint responded, but did not return JSON from /health.");
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ZoomRelayHealthResponse(
                false,
                baseUri.AbsoluteUri,
                null,
                stopwatch.ElapsedMilliseconds,
                "timeout",
                "Timed out waiting for the relay /health endpoint.");
        }
        catch (HttpRequestException ex)
        {
            var errorCode = ClassifyHttpError(ex);
            return new ZoomRelayHealthResponse(
                false,
                baseUri.AbsoluteUri,
                ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null,
                stopwatch.ElapsedMilliseconds,
                errorCode,
                ex.Message);
        }
    }

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

    private Uri? BaseUri() => _settings.GetActiveBaseUri();

    private static string ClassifyHttpError(HttpRequestException ex)
    {
        if (ex.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            return "dns_error";
        }

        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError
            || HasInnerException<AuthenticationException>(ex))
        {
            return "tls_error";
        }

        return "network_error";
    }

    private static bool HasInnerException<TException>(Exception ex) where TException : Exception
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class ZoomRelayException : Exception
{
    public ZoomRelayException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
