using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/meetings")]
public sealed class MeetingsController : ControllerBase
{
    private readonly AttendanceApplicationService _attendanceService;

    public MeetingsController(AttendanceApplicationService attendanceService)
    {
        _attendanceService = attendanceService;
    }

    [HttpGet("{meetingId}/board")]
    public async Task<IActionResult> GetBoard(string meetingId, CancellationToken cancellationToken)
    {
        var board = await _attendanceService.BuildBoardAsync(meetingId, cancellationToken);
        return Ok(board);
    }

    [HttpPost("{meetingId}/seed-demo")]
    public async Task<IActionResult> SeedDemo(string meetingId, CancellationToken cancellationToken)
    {
        await _attendanceService.SeedDemoEventsAsync(meetingId, cancellationToken);
        return Accepted(new { meetingId, message = "Demo events seeded." });
    }

    [HttpGet("{meetingId}/export")]
    public async Task<IActionResult> ExportCsv(string meetingId, CancellationToken cancellationToken)
    {
        var csv = await _attendanceService.BuildBoardCsvAsync(meetingId, cancellationToken);
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"{meetingId}-attendance.csv");
    }
}
