using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Backend.Controllers;

/// <summary>
/// Local dashboard surface for the self-updater. Mutating routes are already
/// restricted to the local dashboard origin by LocalOriginGuardMiddleware.
/// </summary>
[ApiController]
[Route("api/update")]
public sealed class UpdatesController : ControllerBase
{
    private readonly UpdateService _updateService;

    public UpdatesController(UpdateService updateService)
    {
        _updateService = updateService;
    }

    [HttpGet("status")]
    public ActionResult<UpdateStatusResponse> GetStatus()
        => Ok(_updateService.GetStatus());

    [HttpPost("check")]
    public async Task<ActionResult<UpdateStatusResponse>> CheckAsync(CancellationToken cancellationToken)
        => Ok(await _updateService.CheckAsync(manual: true, cancellationToken));

    [HttpPost("install")]
    public async Task<ActionResult<UpdateInstallResponse>> InstallAsync(CancellationToken cancellationToken)
    {
        if (!_updateService.SupportedPlatform)
        {
            return StatusCode(StatusCodes.Status400BadRequest, new UpdateInstallResponse(
                Started: false,
                Version: null,
                ShutdownRequested: false,
                Message: "Automatic install is available on Windows only."));
        }

        var result = await _updateService.InstallAsync(cancellationToken);
        return result.Started
            ? Accepted(result)
            : Conflict(result);
    }
}
