using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Services;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/zoom-app")]
public sealed class ZoomAppBridgeController : ControllerBase
{
    private readonly ZoomAppBridgeService _bridge;
    private readonly ZoomRelayService _relay;
    private readonly AttendanceApplicationService _attendance;

    public ZoomAppBridgeController(
        ZoomAppBridgeService bridge,
        ZoomRelayService relay,
        AttendanceApplicationService attendance)
    {
        _bridge = bridge;
        _relay = relay;
        _attendance = attendance;
    }

    [HttpPost("participants/rename")]
    public async Task<IActionResult> RenameParticipant(
        [FromBody] ZoomParticipantRenameRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !MeetingIdNormalizer.TryNormalize(request.MeetingId, out var meetingId)
            || string.IsNullOrWhiteSpace(request.PresenceKey))
        {
            return Problem(
                title: "Invalid participant rename request.",
                detail: "meetingId and presenceKey are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var status = _relay.GetStatus();
        if (!status.SessionActive || !status.Connected)
        {
            return Problem(
                title: "Zoom App is not connected.",
                detail: "Reconnect the ZoomCheck companion before changing a participant name.",
                statusCode: StatusCodes.Status409Conflict);
        }
        if (!MeetingIdNormalizer.TryNormalize(status.MeetingId, out var connectedMeetingId)
            || !string.Equals(meetingId, connectedMeetingId, StringComparison.Ordinal))
        {
            return Problem(
                title: "Meeting mismatch.",
                detail: "The selected dashboard meeting is not the meeting connected to ZoomCheck.",
                statusCode: StatusCodes.Status409Conflict);
        }
        if (!IsHostRole(status.Role))
        {
            return Problem(
                title: "Host or co-host permission is required.",
                detail: "Only a meeting host or co-host can change another participant's Zoom name.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var presenceKey = request.PresenceKey.Trim();
        const string prefix = "zoom-app:";
        if (!presenceKey.StartsWith(prefix, StringComparison.Ordinal)
            || presenceKey.Length <= prefix.Length
            || presenceKey.Length > 300)
        {
            return UnsafeRename();
        }

        var board = await _attendance.BuildBoardAsync(meetingId, cancellationToken);
        var connection = board.CurrentConnections?.SingleOrDefault(item =>
            string.Equals(item.PresenceKey, presenceKey, StringComparison.Ordinal));
        if (connection is null
            || string.IsNullOrWhiteSpace(connection.MatchedRosterPersonId)
            || string.IsNullOrWhiteSpace(connection.CanonicalName)
            || connection.Confidence is MatchConfidence.Possible or MatchConfidence.Unmatched)
        {
            return UnsafeRename();
        }

        var person = board.People.SingleOrDefault(item =>
            string.Equals(item.RosterPersonId, connection.MatchedRosterPersonId, StringComparison.Ordinal));
        if (person is null
            || string.IsNullOrWhiteSpace(person.Name)
            || person.Name.Length > 128
            || !string.Equals(
                NameNormalizer.Normalize(person.Name),
                NameNormalizer.Normalize(connection.CanonicalName),
                StringComparison.Ordinal)
            || string.Equals(connection.RawName.Trim(), person.Name.Trim(), StringComparison.Ordinal)
            || board.CurrentConnections!.Count(item =>
                string.Equals(item.MatchedRosterPersonId, person.RosterPersonId, StringComparison.Ordinal)) != 1)
        {
            return UnsafeRename();
        }

        try
        {
            var result = await _relay.RenameParticipantAsync(
                presenceKey[prefix.Length..], person.Name.Trim(), cancellationToken);
            if (!result.Success)
            {
                var code = SanitizeErrorCode(result.ErrorCode);
                return Problem(
                    title: "Zoom rejected the participant name change.",
                    detail: string.IsNullOrEmpty(code)
                        ? "Zoom could not change this participant's name. Check meeting permissions and try again."
                        : $"Zoom could not change this participant's name (code {code}).",
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Ok(new ZoomParticipantRenameResponse(
                presenceKey,
                connection.RawName,
                person.Name.Trim(),
                result.CompletedAt ?? DateTimeOffset.UtcNow));
        }
        catch (ZoomAppBridgeException ex)
        {
            return MapProblem(ex);
        }
        catch (ZoomParticipantRenameException ex)
        {
            return Problem(
                title: "Zoom participant name change was not confirmed.",
                detail: ex.Message,
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (ZoomRelayException ex)
        {
            return Problem(
                title: "ZoomCheck relay is unavailable.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost("pairing-code")]
    public async Task<ActionResult<ZoomAppPairingCodeResponse>> CreatePairingCode(
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(_relay.IsConfigured
                ? await _relay.CreatePairingCodeAsync(cancellationToken)
                : _bridge.CreatePairingCode());
        }
        catch (ZoomRelayException ex)
        {
            return Problem(
                title: "ZoomCheck relay is unavailable.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost("sync")]
    public async Task<IActionResult> RequestSync(CancellationToken cancellationToken)
    {
        try
        {
            var revision = _relay.GetStatus().SessionActive
                ? await _relay.RequestSyncAsync(cancellationToken)
                : _bridge.RequestSync();
            return Accepted(new { requestedRevision = revision });
        }
        catch (ZoomAppBridgeException ex)
        {
            return MapProblem(ex);
        }
        catch (ZoomRelayException ex)
        {
            return Problem(
                title: "ZoomCheck relay is unavailable.",
                detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost("bridge/connect")]
    public IActionResult Connect([FromBody] ZoomAppConnectRequest request)
    {
        try
        {
            return Ok(_bridge.Connect(request));
        }
        catch (ZoomAppBridgeException ex)
        {
            return MapProblem(ex);
        }
    }

    [HttpPost("bridge/snapshot")]
    public async Task<IActionResult> Snapshot(
        [FromBody] ZoomAppSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _bridge.ApplySnapshotAsync(request, cancellationToken));
        }
        catch (ZoomAppBridgeException ex)
        {
            return MapProblem(ex);
        }
    }

    [HttpPost("bridge/heartbeat")]
    public IActionResult Heartbeat([FromBody] ZoomAppHeartbeatRequest request)
    {
        try
        {
            return Ok(_bridge.Heartbeat(request));
        }
        catch (ZoomAppBridgeException ex)
        {
            return MapProblem(ex);
        }
    }

    private ObjectResult MapProblem(ZoomAppBridgeException ex)
    {
        var (status, title) = ex.Error switch
        {
            ZoomAppBridgeError.InvalidPairingCode => (StatusCodes.Status401Unauthorized, "Zoom App pairing failed."),
            ZoomAppBridgeError.InvalidSession => (StatusCodes.Status401Unauthorized, "Zoom App session is invalid."),
            ZoomAppBridgeError.InsufficientRole => (StatusCodes.Status403Forbidden, "Host or co-host permission is required."),
            ZoomAppBridgeError.UnsupportedClient => (StatusCodes.Status412PreconditionFailed, "Required Zoom Apps SDK APIs are unavailable."),
            ZoomAppBridgeError.NotConnected => (StatusCodes.Status409Conflict, "Zoom App is not connected."),
            ZoomAppBridgeError.UnconfirmedEmptySnapshot => (StatusCodes.Status409Conflict, "Empty Zoom App snapshot needs confirmation."),
            _ => (StatusCodes.Status400BadRequest, "Invalid Zoom App bridge request.")
        };

        return Problem(title: title, detail: ex.Message, statusCode: status);
    }

    private ObjectResult UnsafeRename()
        => Problem(
            title: "Participant name cannot be changed safely.",
            detail: "ZoomCheck only changes a name when one active Zoom connection uniquely matches one roster person.",
            statusCode: StatusCodes.Status409Conflict);

    private static bool IsHostRole(string? role)
        => string.Equals(role, "host", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "cohost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "co-host", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeErrorCode(string? value)
        => new((value ?? string.Empty)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            .Take(24)
            .ToArray());
}
