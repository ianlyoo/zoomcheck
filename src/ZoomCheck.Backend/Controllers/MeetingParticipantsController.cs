using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/meetings/{meetingId}")]
public sealed class MeetingParticipantsController(AttendanceApplicationService attendanceService) : ControllerBase
{
    [HttpPut("people/{personId}/exclusion")]
    public Task<IActionResult> SetExclusion(string meetingId, string personId, [FromBody] MeetingExclusionRequest? request, CancellationToken cancellationToken)
        => ExecuteAsync(meetingId, normalized => request?.Excluded is bool excluded
            ? attendanceService.SetMeetingExclusionAsync(normalized, personId, excluded, cancellationToken, request.AttendanceDate)
            : throw new ArgumentException("제외 또는 복원 여부를 지정하세요."));

    [HttpPut("people/{personId}/review")]
    public Task<IActionResult> SetPersonReview(string meetingId, string personId, [FromBody] MeetingPersonReviewRequest? request, CancellationToken cancellationToken)
        => ExecuteAsync(meetingId, normalized => request is not null
            ? attendanceService.SetPersonReviewAsync(normalized, personId, request.Kind ?? "", request.Status ?? "", request.ExpectedEvidenceToken ?? "", cancellationToken)
            : throw new ArgumentException("검토 내용을 지정하세요."));

    [HttpPut("connections/match")]
    public Task<IActionResult> SetConnectionMatch(string meetingId, [FromBody] MeetingConnectionMatchRequest? request, CancellationToken cancellationToken)
        => ExecuteAsync(meetingId, normalized => request is not null
            ? attendanceService.SetConnectionMatchAsync(normalized, request.Source ?? "", request.PresenceKey ?? "", request.RosterPersonId, request.ExpectedEvidenceToken ?? "", cancellationToken)
            : throw new ArgumentException("연결할 참가자를 지정하세요."));

    [HttpPut("connections/review")]
    public Task<IActionResult> SetConnectionReview(string meetingId, [FromBody] MeetingConnectionReviewRequest? request, CancellationToken cancellationToken)
        => ExecuteAsync(meetingId, normalized => request is not null
            ? attendanceService.SetConnectionReviewAsync(normalized, request.Source ?? "", request.PresenceKey ?? "", request.Status ?? "", request.ExpectedEvidenceToken ?? "", cancellationToken)
            : throw new ArgumentException("보류할 참가자를 지정하세요."));

    private async Task<IActionResult> ExecuteAsync(string meetingId, Func<string, Task<AttendanceBoard>> action)
    {
        if (!MeetingIdNormalizer.TryNormalize(meetingId, out var normalized))
        {
            return Problem(title: "회의 ID를 확인하세요.", statusCode: StatusCodes.Status400BadRequest);
        }
        try { return Ok(await action(normalized)); }
        catch (ArgumentException ex) { return Problem(title: "입력 내용을 확인하세요.", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (KeyNotFoundException ex) { return Problem(title: "참가자를 찾을 수 없습니다.", detail: ex.Message, statusCode: StatusCodes.Status404NotFound); }
        catch (InvalidOperationException ex) { return Problem(title: "현황을 다시 확인하세요.", detail: ex.Message, statusCode: StatusCodes.Status409Conflict); }
    }
}
