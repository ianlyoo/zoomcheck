using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Backend.Services;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/zoom/webhooks")]
public sealed class ZoomWebhooksController : ControllerBase
{
    private readonly AttendanceApplicationService _attendanceService;
    private readonly ZoomWebhookValidator _webhookValidator;

    public ZoomWebhooksController(AttendanceApplicationService attendanceService, ZoomWebhookValidator webhookValidator)
    {
        _attendanceService = attendanceService;
        _webhookValidator = webhookValidator;
    }

    [HttpPost("participant-event")]
    public async Task<IActionResult> RecordParticipantEvent([FromBody] ZoomWebhookRequest request, CancellationToken cancellationToken)
    {
        var eventType = request.EventType.Equals("left", StringComparison.OrdinalIgnoreCase)
            ? ParticipantEventType.Left
            : ParticipantEventType.Joined;

        var recorded = await _attendanceService.RecordZoomEventAsync(
            new ZoomParticipantEventInput(
                request.MeetingId,
                request.OccurredAt ?? DateTimeOffset.UtcNow,
                eventType,
                request.ParticipantName,
                request.ParticipantEmail,
                string.IsNullOrWhiteSpace(request.Source) ? "manual-api" : request.Source,
                JsonSerializer.Serialize(request)),
            cancellationToken);

        return Ok(recorded);
    }

    [HttpPost("raw" )]
    public async Task<IActionResult> RecordRawZoomPayload([FromBody] JsonElement payload, CancellationToken cancellationToken)
    {
        if (!TryMapRawPayload(payload, out var mappedRequest))
        {
            return BadRequest(new { message = "Payload did not contain recognizable meeting/participant fields." });
        }

        var recorded = await _attendanceService.RecordZoomEventAsync(
            new ZoomParticipantEventInput(
                mappedRequest.MeetingId,
                mappedRequest.OccurredAt ?? DateTimeOffset.UtcNow,
                mappedRequest.EventType.Equals("left", StringComparison.OrdinalIgnoreCase) ? ParticipantEventType.Left : ParticipantEventType.Joined,
                mappedRequest.ParticipantName,
                mappedRequest.ParticipantEmail,
                mappedRequest.Source,
                payload.GetRawText()),
            cancellationToken);

        return Ok(recorded);
    }

    [HttpPost("events")]
    public async Task<IActionResult> ReceiveZoomEvent(CancellationToken cancellationToken)
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);
        Request.Body.Position = 0;

        JsonDocument jsonDocument;
        try
        {
            jsonDocument = JsonDocument.Parse(rawBody);
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "Zoom webhook body was not valid JSON." });
        }

        using (jsonDocument)
        {
            var payload = jsonDocument.RootElement;
            if (_webhookValidator.IsEndpointValidation(payload, out var validationResponse))
            {
                return Ok(new
                {
                    plainToken = validationResponse.PlainToken,
                    encryptedToken = validationResponse.EncryptedToken
                });
            }

            if (!_webhookValidator.TryValidate(
                    rawBody,
                    Request.Headers["x-zm-request-timestamp"].ToString(),
                    Request.Headers["x-zm-signature"].ToString(),
                    out var failureReason))
            {
                return Unauthorized(new { message = failureReason });
            }

            if (!TryMapRawPayload(payload, out var mappedRequest))
            {
                return Ok(new { message = "Zoom event acknowledged but not mapped into attendance tracking." });
            }

            var recorded = await _attendanceService.RecordZoomEventAsync(
                new ZoomParticipantEventInput(
                    mappedRequest.MeetingId,
                    mappedRequest.OccurredAt ?? DateTimeOffset.UtcNow,
                    mappedRequest.EventType.Equals("left", StringComparison.OrdinalIgnoreCase) ? ParticipantEventType.Left : ParticipantEventType.Joined,
                    mappedRequest.ParticipantName,
                    mappedRequest.ParticipantEmail,
                    "zoom-webhook",
                    rawBody),
                cancellationToken);

            return Ok(new { recorded.Id, recorded.MeetingId, recorded.Confidence, recorded.MatchedRosterPersonId });
        }
    }

    private static bool TryMapRawPayload(JsonElement payload, out ZoomWebhookRequest request)
    {
        request = null!;
        var eventType = payload.TryGetProperty("event", out var eventProperty)
            ? eventProperty.GetString() ?? string.Empty
            : string.Empty;

        if (!payload.TryGetProperty("payload", out var payloadNode) || !payloadNode.TryGetProperty("object", out var objectNode))
        {
            return false;
        }

        var meetingId = objectNode.TryGetProperty("id", out var meetingNode)
            ? meetingNode.ToString()
            : string.Empty;
        var participantNode = objectNode.TryGetProperty("participant", out var participant)
            ? participant
            : default;
        var participantName = participantNode.ValueKind != JsonValueKind.Undefined && participantNode.TryGetProperty("user_name", out var nameNode)
            ? nameNode.GetString() ?? string.Empty
            : string.Empty;
        var participantEmail = participantNode.ValueKind != JsonValueKind.Undefined && participantNode.TryGetProperty("email", out var emailNode)
            ? emailNode.GetString()
            : null;

        var occurredAt = objectNode.TryGetProperty("start_time", out var startNode) && DateTimeOffset.TryParse(startNode.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        if (string.IsNullOrWhiteSpace(meetingId) || string.IsNullOrWhiteSpace(participantName))
        {
            return false;
        }

        request = new ZoomWebhookRequest(
            meetingId,
            eventType.Contains("left", StringComparison.OrdinalIgnoreCase) ? "left" : "joined",
            participantName,
            participantEmail,
            occurredAt,
            "zoom-webhook-raw");

        return true;
    }
}
