using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Core.Models;
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

    [HttpPost("{meetingId}/events")]
    public async Task<IActionResult> IngestParticipantEvents(string meetingId, [FromBody] IReadOnlyList<ParticipantEventRequest> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return Ok(new { accepted = 0 });
        }

        var accepted = 0;
        foreach (var item in events)
        {
            await _attendanceService.RecordParticipantEventAsync(
                new ParticipantEventInput(
                    meetingId,
                    item.OccurredAt ?? DateTimeOffset.UtcNow,
                    item.EventType,
                    item.ParticipantName,
                    item.ParticipantEmail,
                    string.IsNullOrWhiteSpace(item.Source) ? "panel-observation" : item.Source,
                    string.IsNullOrWhiteSpace(item.RawPayload) ? "{}" : item.RawPayload),
                cancellationToken);
            accepted++;
        }

        return Ok(new { accepted });
    }

    [HttpGet("{meetingId}/export")]
    public async Task<IActionResult> ExportCsv(string meetingId, CancellationToken cancellationToken)
    {
        var csv = await _attendanceService.BuildBoardCsvAsync(meetingId, cancellationToken);
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"{meetingId}-attendance.csv");
    }
}
