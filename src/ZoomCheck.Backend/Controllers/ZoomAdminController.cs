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

    public ZoomAdminController(IOptions<ZoomOptions> options, ZoomOAuthTokenService tokenService)
    {
        _options = options.Value;
        _tokenService = tokenService;
    }

    [HttpGet("settings-status")]
    public async Task<ActionResult<ZoomSettingsStatusResponse>> GetSettingsStatus(CancellationToken cancellationToken)
    {
        var token = await _tokenService.TryGetAccessTokenAsync(cancellationToken);
        var missingFields = new List<string>();

        if (string.IsNullOrWhiteSpace(_options.WebhookSecretToken))
        {
            missingFields.Add("Zoom:WebhookSecretToken");
        }

        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            missingFields.Add("Zoom:ClientId");
        }

        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            missingFields.Add("Zoom:ClientSecret");
        }

        if (string.IsNullOrWhiteSpace(_options.AccountId))
        {
            missingFields.Add("Zoom:AccountId");
        }

        return Ok(new ZoomSettingsStatusResponse(
            WebhookSecretConfigured: !string.IsNullOrWhiteSpace(_options.WebhookSecretToken),
            OAuthConfigured: _tokenService.IsConfigured,
            TokenAvailable: !string.IsNullOrWhiteSpace(token),
            TokenExpiresAt: _tokenService.ExpiresAt,
            MissingFields: missingFields.ToArray()));
    }
}
