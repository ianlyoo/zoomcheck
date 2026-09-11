using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/settings/zoom-relay")]
public sealed class ZoomRelaySettingsController : ControllerBase
{
    private readonly ZoomRelaySettingsStore _store;

    public ZoomRelaySettingsController(ZoomRelaySettingsStore store)
    {
        _store = store;
    }

    [HttpGet]
    public ActionResult<ZoomRelaySettingsResponse> Get()
    {
        var value = _store.Get();
        return Ok(new ZoomRelaySettingsResponse(value.Configured, value.BaseUrl, value.RestartRequired));
    }

    [HttpPut]
    public async Task<ActionResult<ZoomRelaySettingsResponse>> Put(
        [FromBody] UpdateZoomRelaySettingsRequest? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var value = await _store.SaveAsync(request?.BaseUrl, cancellationToken);
            return Ok(new ZoomRelaySettingsResponse(value.Configured, value.BaseUrl, value.RestartRequired));
        }
        catch (ArgumentException ex)
        {
            return Problem(
                title: "Invalid ZoomCheck relay URL.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
