using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/roster")]
public sealed class RosterController : ControllerBase
{
    private readonly AttendanceApplicationService _attendanceService;

    public RosterController(AttendanceApplicationService attendanceService)
    {
        _attendanceService = attendanceService;
    }

    [HttpGet]
    public async Task<IActionResult> GetRoster(CancellationToken cancellationToken)
    {
        var roster = await _attendanceService.GetRosterAsync(cancellationToken);
        return Ok(roster);
    }

    [HttpPost("import")]
    public async Task<IActionResult> Import([FromBody] RosterImportRequest request, CancellationToken cancellationToken)
    {
        var imported = await _attendanceService.ImportRosterAsync(request.FilePath, cancellationToken);
        return Ok(new { imported.ImportId, imported.DisplayName, Count = imported.People.Count, imported.ImportedAt });
    }

    [HttpPost("alias")]
    public async Task<IActionResult> SaveAlias([FromBody] AliasRequest request, CancellationToken cancellationToken)
    {
        await _attendanceService.SaveAliasAsync(request.Alias, request.RosterPersonId, request.Note, cancellationToken);
        return Ok(new { message = "Alias saved." });
    }
}
