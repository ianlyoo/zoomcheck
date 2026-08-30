using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/meetings")]
public sealed class MeetingsController : ControllerBase
{
    private const string DefaultSnapshotSource = "manual-snapshot";
    private const int MaxSnapshotParticipants = 2000;

    /// <summary>
    /// Roster groups are short labels such as "1조" or "A반". Capping the query keeps an
    /// oversized value from travelling into the export path and the file name.
    /// </summary>
    private const int MaxGroupFilterLength = 100;

    private readonly AttendanceApplicationService _attendanceService;

    public MeetingsController(AttendanceApplicationService attendanceService)
    {
        _attendanceService = attendanceService;
    }

    [HttpGet("{meetingId}/board")]
    public async Task<IActionResult> GetBoard(string meetingId, CancellationToken cancellationToken)
    {
        if (!MeetingIdNormalizer.TryNormalize(meetingId, out var normalizedMeetingId))
        {
            return InvalidMeetingId();
        }
        var board = await _attendanceService.BuildBoardAsync(normalizedMeetingId, cancellationToken);
        return Ok(board);
    }

    [HttpPost("{meetingId}/seed-demo")]
    public async Task<IActionResult> SeedDemo(string meetingId, CancellationToken cancellationToken)
    {
        if (!MeetingIdNormalizer.TryNormalize(meetingId, out var normalizedMeetingId))
        {
            return InvalidMeetingId();
        }
        await _attendanceService.SeedDemoEventsAsync(normalizedMeetingId, cancellationToken);
        return Accepted(new { meetingId = normalizedMeetingId, message = "Demo events seeded." });
    }

    [HttpPost("{meetingId}/events")]
    public async Task<IActionResult> IngestParticipantEvents(string meetingId, [FromBody] IReadOnlyList<ParticipantEventRequest> events, CancellationToken cancellationToken)
    {
        if (!MeetingIdNormalizer.TryNormalize(meetingId, out var normalizedMeetingId))
        {
            return InvalidMeetingId();
        }
        if (events.Count == 0)
        {
            return Ok(new { accepted = 0 });
        }

        var accepted = 0;
        foreach (var item in events)
        {
            await _attendanceService.RecordParticipantEventAsync(
                new ParticipantEventInput(
                    normalizedMeetingId,
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

    [HttpPost("{meetingId}/participant-snapshot")]
    [ProducesResponseType(typeof(ParticipantSnapshotResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ApplyParticipantSnapshot(string meetingId, [FromBody] ParticipantSnapshotRequest? request, CancellationToken cancellationToken)
    {
        if (!MeetingIdNormalizer.TryNormalize(meetingId, out var normalizedMeetingId))
        {
            return InvalidMeetingId();
        }

        if (request is null)
        {
            return Problem(
                title: "Invalid snapshot payload.",
                detail: "A JSON body with participantNames is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.ParticipantNames is null)
        {
            return Problem(
                title: "Invalid snapshot payload.",
                detail: "participantNames is required. Send an empty array to record that nobody is present.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.ParticipantNames.Count > MaxSnapshotParticipants)
        {
            return Problem(
                title: "Snapshot too large.",
                detail: $"participantNames cannot contain more than {MaxSnapshotParticipants} entries.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var source = string.IsNullOrWhiteSpace(request.Source) ? DefaultSnapshotSource : request.Source.Trim();

        var connections = request.Participants?
            .Where(participant => !string.IsNullOrWhiteSpace(participant.PresenceKey) && !string.IsNullOrWhiteSpace(participant.DisplayName))
            .Select(participant => new ParticipantSnapshotParticipant(
                participant.PresenceKey!.Trim(),
                participant.DisplayName!.Trim(),
                string.IsNullOrWhiteSpace(participant.Email) ? null : participant.Email.Trim()))
            .ToArray();

        var result = await _attendanceService.ApplyParticipantSnapshotAsync(
            new ParticipantSnapshotInput(
                normalizedMeetingId,
                request.ParticipantNames,
                source,
                request.CapturedAt ?? DateTimeOffset.UtcNow,
                ParticipantEmails: null,
                Participants: connections is { Length: > 0 } ? connections : null),
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{meetingId}/export")]
    public async Task<IActionResult> ExportCsv(
        string meetingId,
        [FromQuery(Name = "group")] string? group,
        CancellationToken cancellationToken)
    {
        if (!MeetingIdNormalizer.TryNormalize(meetingId, out var normalizedMeetingId))
        {
            return InvalidMeetingId();
        }

        if (group is { Length: > MaxGroupFilterLength })
        {
            return Problem(
                title: "Invalid group filter.",
                detail: $"group cannot be longer than {MaxGroupFilterLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var csv = await _attendanceService.BuildBoardCsvAsync(normalizedMeetingId, group, cancellationToken);
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"{normalizedMeetingId}-attendance.csv");
    }

    private ObjectResult InvalidMeetingId()
        => Problem(
            title: "Invalid meeting id.",
            detail: "meetingId is required and must be valid. Spaces and hyphens in numeric Zoom meeting IDs are ignored.",
            statusCode: StatusCodes.Status400BadRequest);
}
