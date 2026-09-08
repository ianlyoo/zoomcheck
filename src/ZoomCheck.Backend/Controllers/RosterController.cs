using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/roster")]
public sealed class RosterController : ControllerBase
{
    private const long MaxRosterFileBytes = 20 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx",
        ".xls"
    };

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

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRosterFileBytes)]
    public async Task<IActionResult> Upload([FromForm] IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return Problem(
                title: "명단 파일이 필요합니다.",
                detail: "Excel .xlsx 또는 .xls 파일을 선택해 주세요.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxRosterFileBytes)
        {
            return Problem(
                title: "명단 파일이 너무 큽니다.",
                detail: "20MB 이하의 Excel 파일을 사용해 주세요.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var displayName = Path.GetFileName(file.FileName);
        if (!AllowedExtensions.Contains(Path.GetExtension(displayName)))
        {
            return Problem(
                title: "지원하지 않는 파일 형식입니다.",
                detail: "Excel .xlsx 또는 .xls 파일만 업로드할 수 있습니다.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var imported = await _attendanceService.ImportRosterAsync(stream, displayName, cancellationToken);
            return Ok(new
            {
                imported.ImportId,
                imported.DisplayName,
                Count = imported.People.Count,
                imported.ImportedAt,
                SessionReset = false
            });
        }
        catch (InvalidOperationException ex)
        {
            return Problem(
                title: "명단을 읽을 수 없습니다.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (IOException ex)
        {
            return Problem(
                title: "명단 파일을 열 수 없습니다.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    [HttpPost("alias")]
    public async Task<IActionResult> SaveAlias([FromBody] AliasRequest request, CancellationToken cancellationToken)
    {
        await _attendanceService.SaveAliasAsync(request.Alias, request.RosterPersonId, request.Note, cancellationToken);
        return Ok(new { message = "Alias saved." });
    }
}
