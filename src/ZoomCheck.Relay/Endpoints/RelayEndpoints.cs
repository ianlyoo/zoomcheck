using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Relay.Contracts;
using ZoomCheck.Relay.Services;

namespace ZoomCheck.Relay.Endpoints;

/// <summary>
/// HTTP surface for the relay. Handlers are thin: they extract the bearer token, delegate to the
/// store, and map a <see cref="RelayStatus"/> onto a status code. No handler logs pairing codes,
/// tokens, public keys, nonces, or ciphertext.
/// </summary>
public static class RelayEndpoints
{
    private const string BearerPrefix = "Bearer ";

    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", (RelaySessionStore store, TimeProvider clock) =>
            Results.Ok(new RelayHealthResponse(
                "ok",
                store.ActiveSessionCount,
                store.Options.MaxSessions,
                clock.GetUtcNow())));

        var pairings = app.MapGroup("/api/v1/pairings");

        pairings.MapPost("/", (
            [FromBody] CreatePairingRequest request,
            RelaySessionStore store) => ToResult(store.CreatePairing(request)));

        pairings.MapPost("/redeem", (
            [FromBody] RedeemPairingRequest request,
            RelaySessionStore store,
            RelayRateLimiter limiter,
            HttpContext http) =>
        {
            var key = ClientKey(http);

            // Guess budget is checked before touching the store so a brute-force loop cannot
            // use timing of store lookups as an oracle.
            if (!limiter.PairingAttemptAllowed(key))
            {
                return Problem(StatusCodes.Status429TooManyRequests, "too_many_pairing_attempts");
            }

            var result = store.RedeemPairing(request);
            if (result.Status is RelayStatus.NotFound or RelayStatus.Invalid)
            {
                limiter.RecordPairingFailure(key);
            }

            return ToResult(result);
        });

        MapDirection(app, "desktop", RelayParticipant.Desktop);
        MapDirection(app, "companion", RelayParticipant.Companion);

        return app;
    }

    private static void MapDirection(IEndpointRouteBuilder app, string segment, RelayParticipant participant)
    {
        var group = app.MapGroup($"/api/v1/sessions/{{sessionId}}/{segment}/messages");

        group.MapGet("/", (
            string sessionId,
            [FromQuery] long? afterSequence,
            RelaySessionStore store,
            HttpContext http) => ToResult(store.Poll(
                sessionId,
                participant,
                ReadBearerToken(http),
                afterSequence ?? 0)));

        group.MapPost("/", (
            string sessionId,
            [FromBody] RelayEnvelope envelope,
            RelaySessionStore store,
            HttpContext http) => ToResult(store.Publish(
                sessionId,
                participant,
                ReadBearerToken(http),
                envelope)));
    }

    /// <summary>Extracts a bearer token, returning null for any malformed header.</summary>
    internal static string? ReadBearerToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var token = header[BearerPrefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// Rate-limit identity. Remote IP is the only trustworthy signal here; forwarded headers are
    /// intentionally ignored because a client can forge them to escape its own budget.
    /// </summary>
    internal static string ClientKey(HttpContext http)
        => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static IResult ToResult<T>(RelayResult<T> result)
    {
        if (result.IsOk)
        {
            return Results.Ok(result.Value);
        }

        var (status, reason) = result.Status switch
        {
            RelayStatus.Invalid => (StatusCodes.Status400BadRequest, result.Reason ?? "invalid_request"),
            RelayStatus.CodeConflict => (StatusCodes.Status409Conflict, result.Reason ?? "pairing_code_in_use"),
            RelayStatus.NotFound => (StatusCodes.Status404NotFound, result.Reason ?? "not_found"),
            RelayStatus.Unauthorized => (StatusCodes.Status401Unauthorized, result.Reason ?? "unauthorized"),
            RelayStatus.PeerAbsent => (StatusCodes.Status409Conflict, result.Reason ?? "peer_absent"),
            RelayStatus.SequenceRejected => (StatusCodes.Status409Conflict, result.Reason ?? "sequence_not_monotonic"),
            RelayStatus.QueueFull => (StatusCodes.Status429TooManyRequests, result.Reason ?? "queue_full"),
            RelayStatus.CapacityExceeded => (StatusCodes.Status503ServiceUnavailable, result.Reason ?? "relay_at_capacity"),
            _ => (StatusCodes.Status500InternalServerError, "unexpected_error"),
        };

        return Problem(status, reason);
    }

    private static IResult Problem(int statusCode, string reason)
        => Results.Json(new { error = reason }, statusCode: statusCode);
}
