using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/zoom-app")]
public sealed class ZoomAppBridgeController : ControllerBase
{
    private readonly ZoomAppBridgeService _bridge;
    private readonly ZoomRelayService _relay;

    public ZoomAppBridgeController(ZoomAppBridgeService bridge, ZoomRelayService relay)
    {
        _bridge = bridge;
        _relay = relay;
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
            var revision = _relay.GetStatus().Connected
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
}
