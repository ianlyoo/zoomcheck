using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/zoom")]
public sealed class ZoomAdminController : ControllerBase
{
    private readonly ZoomOptions _options;
    private readonly ZoomOAuthTokenService _tokenService;
    private readonly ZoomRecoveryService _recoveryService;

    public ZoomAdminController(
        IOptions<ZoomOptions> options,
        ZoomOAuthTokenService tokenService,
        ZoomRecoveryService recoveryService)
    {
        _options = options.Value;
        _tokenService = tokenService;
        _recoveryService = recoveryService;
    }

    [HttpGet("settings-status")]
    public async Task<ActionResult<ZoomSettingsStatusResponse>> GetSettingsStatus(CancellationToken cancellationToken)
    {
        string? token = null;
        try
        {
            token = await _tokenService.TryGetAccessTokenAsync(cancellationToken);
        }
        catch
        {
            token = null;
        }
        var missingFields = new List<string>();

        if (!HasConfiguredValue(_options.WebhookSecretToken))
        {
            missingFields.Add("Zoom:WebhookSecretToken");
        }

        if (!HasConfiguredValue(_options.ClientId))
        {
            missingFields.Add("Zoom:ClientId");
        }

        if (!HasConfiguredValue(_options.ClientSecret))
        {
            missingFields.Add("Zoom:ClientSecret");
        }

        if (!HasConfiguredValue(_options.AccountId))
        {
            missingFields.Add("Zoom:AccountId");
        }

        return Ok(new ZoomSettingsStatusResponse(
            WebhookSecretConfigured: HasConfiguredValue(_options.WebhookSecretToken),
            OAuthConfigured: _tokenService.IsConfigured,
            TokenAvailable: !string.IsNullOrWhiteSpace(token),
            TokenExpiresAt: _tokenService.ExpiresAt,
            MissingFields: missingFields.ToArray()));
    }

    [HttpGet("recovery/last")]
    public ActionResult<ZoomRecoveryLastRun> GetRecoveryLastRun()
    {
        var last = _recoveryService.GetLastRun();
        return Ok(last);
    }

    [HttpPost("recovery/run")]
    public async Task<ActionResult<ZoomRecoveryResult>> RunRecoveryNow(CancellationToken cancellationToken)
    {
        var result = await _recoveryService.RecoverLiveMeetingsAsync(cancellationToken);
        return Ok(result);
    }

    private static bool HasConfiguredValue(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.StartsWith("replace-with-your-", StringComparison.OrdinalIgnoreCase);
}
