using System;

namespace ZoomCheck.App.Models;

public sealed record ParticipantPanelStatus(
    bool IsAttached,
    bool IsMonitoring,
    int VisibleParticipantCount,
    string Message,
    DateTimeOffset UpdatedAt);

public sealed record ParticipantPanelActionResult(
    bool Success,
    int VisibleParticipantCount,
    string Message);
