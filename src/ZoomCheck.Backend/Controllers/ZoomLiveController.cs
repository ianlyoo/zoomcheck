using System.Net;
using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/zoom")]
public sealed class ZoomLiveController : ControllerBase
{
    private readonly ZoomLiveSyncService _liveSyncService;
    private readonly ZoomOAuthTokenService _tokenService;

    public ZoomLiveController(ZoomLiveSyncService liveSyncService, ZoomOAuthTokenService tokenService)
    {
        _liveSyncService = liveSyncService;
        _tokenService = tokenService;
    }

    [HttpGet("connection-status")]
    public ActionResult GetConnectionStatus()
    {
        return Ok(new
        {
            configured = _tokenService.IsConfigured,
            tokenCached = _tokenService.HasUsableCachedToken,
            tokenExpiresAt = _tokenService.ExpiresAt,
            requiredScope = "dashboard:read:list_meeting_participants:admin",
            classicScope = "dashboard_meetings:read:admin",
            endpoint = "GET /v2/metrics/meetings/{meetingId}/participants?type=live"
        });
    }

    [HttpPost("meetings/{meetingId}/sync")]
    [ProducesResponseType(typeof(ZoomLiveSyncResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> SyncLiveParticipants(
        string meetingId,
        [FromQuery] bool allowEmptySnapshot,
        CancellationToken cancellationToken)
    {
        if (!_tokenService.IsConfigured)
        {
            return Problem(
                title: "Zoom API credentials are not configured.",
                detail: "Set ZOOMCHECK_Zoom__AccountId, ZOOMCHECK_Zoom__ClientId, and ZOOMCHECK_Zoom__ClientSecret, then restart ZoomCheck.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return Ok(await _liveSyncService.SyncAsync(meetingId, allowEmptySnapshot, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Problem(
                title: "Invalid Zoom meeting id.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (ZoomLiveSyncException ex)
        {
            return Problem(
                title: "No active Zoom participants were returned.",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (ZoomOAuthException ex)
        {
            return Problem(
                title: "Zoom OAuth authentication failed.",
                detail: ex.UserMessage,
                statusCode: StatusCodes.Status502BadGateway);
        }
        catch (ZoomApiException ex)
        {
            return MapZoomApiProblem(ex);
        }
    }

    private ObjectResult MapZoomApiProblem(ZoomApiException ex)
    {
        var (status, title, detail) = ex.StatusCode switch
        {
            HttpStatusCode.Unauthorized => (
                StatusCodes.Status401Unauthorized,
                "Zoom rejected the OAuth token.",
                "Restart ZoomCheck and verify the Server-to-Server OAuth app credentials."),
            HttpStatusCode.Forbidden => (
                StatusCodes.Status403Forbidden,
                "Zoom API permission is missing.",
                "Add dashboard:read:list_meeting_participants:admin (or the classic dashboard_meetings:read:admin scope) to the Server-to-Server OAuth app and confirm the account has Zoom Dashboard access."),
            HttpStatusCode.NotFound => (
                StatusCodes.Status404NotFound,
                "The live Zoom meeting was not found.",
                "Verify the meeting ID, confirm the meeting is currently live, and make sure it belongs to the OAuth app account."),
            HttpStatusCode.TooManyRequests => (
                StatusCodes.Status429TooManyRequests,
                "Zoom API rate limit reached.",
                "Increase the synchronization interval and try again shortly."),
            _ => (
                StatusCodes.Status502BadGateway,
                "Zoom API request failed.",
                $"Zoom returned HTTP {(int)ex.StatusCode}. The existing attendance snapshot was left unchanged.")
        };

        return Problem(title: title, detail: detail, statusCode: status);
    }
}
