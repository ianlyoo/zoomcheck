using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Services;

public sealed partial class AttendanceApplicationService
{
    private string CurrentAttendanceDate() => _timeProvider.GetLocalNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public Task<AttendanceBoard> SetMeetingExclusionAsync(string meetingId, string personId, bool excluded, CancellationToken cancellationToken = default, string? attendanceDate = null)
    {
        meetingId = MeetingIdNormalizer.Normalize(meetingId);
        return WithMeetingLockAsync(meetingId, async () =>
        {
            await RequireRosterPersonAsync(personId, cancellationToken);
            var currentDate = CurrentAttendanceDate();
            if (attendanceDate is not null && attendanceDate != currentDate) { throw new InvalidOperationException("날짜가 변경되었습니다. 오늘 현황을 다시 읽고 적용하세요."); }
            await _repository.SetMeetingExclusionAsync(meetingId, currentDate, personId, excluded, cancellationToken);
            return await BuildBoardAsync(meetingId, cancellationToken);
        }, cancellationToken);
    }

    public Task<AttendanceBoard> SetPersonReviewAsync(string meetingId, string personId, string kind, string status, string evidenceToken, CancellationToken cancellationToken = default)
    {
        if (kind is not ("identity" or "duplicate")) { throw new ArgumentException("검토 종류가 올바르지 않습니다."); }
        ValidateReviewStatus(status, allowConfirmed: true);
        meetingId = MeetingIdNormalizer.Normalize(meetingId);
        return WithMeetingLockAsync(meetingId, async () =>
        {
            var board = await BuildBoardAsync(meetingId, cancellationToken);
            var person = board.People.SingleOrDefault(person => person.RosterPersonId == personId)
                ?? throw new KeyNotFoundException("명단에서 해당 참가자를 찾을 수 없습니다.");
            if (person.IsExcluded) { throw new InvalidOperationException("제외된 참가자를 먼저 복원하세요."); }
            var currentStatus = kind == "identity" ? person.IdentityReviewStatus : person.DuplicateReviewStatus;
            if (currentStatus == "none") { throw new InvalidOperationException("현재 이 항목은 검토 대상이 아닙니다."); }
            RequireEvidence(evidenceToken, kind == "identity" ? person.IdentityEvidenceToken : person.DuplicateEvidenceToken);
            await _repository.SaveMeetingReviewAsync(meetingId,
                new(personId, kind, status, evidenceToken, DateTimeOffset.UtcNow), cancellationToken);
            return await BuildBoardAsync(meetingId, cancellationToken);
        }, cancellationToken);
    }

    public Task<AttendanceBoard> SetConnectionMatchAsync(string meetingId, string source, string presenceKey, string? personId, string evidenceToken, CancellationToken cancellationToken = default)
    {
        ValidateConnectionKey(source, presenceKey);
        meetingId = MeetingIdNormalizer.Normalize(meetingId);
        return WithMeetingLockAsync(meetingId, async () =>
        {
            var board = await BuildBoardAsync(meetingId, cancellationToken);
            var connection = RequireCurrentConnection(board, source, presenceKey);
            RequireEvidence(evidenceToken, connection.EvidenceToken);
            if (personId is not null)
            {
                await RequireRosterPersonAsync(personId, cancellationToken);
                if (board.People.Any(person => person.RosterPersonId == personId && person.IsExcluded))
                {
                    throw new InvalidOperationException("선택한 명단 인물을 먼저 복원하세요.");
                }
            }
            var match = new MeetingConnectionMatch(source, presenceKey, personId ?? "", connection.RawName,
                AttendanceBoardProjector.NormalizeEmail(connection.Email), connection.FirstSeenAt, connection.LastSeenAt);
            await _repository.SaveMeetingConnectionMatchAsync(meetingId, match, remove: personId is null, cancellationToken);
            return await BuildBoardAsync(meetingId, cancellationToken);
        }, cancellationToken);
    }

    public Task<AttendanceBoard> SetConnectionReviewAsync(string meetingId, string source, string presenceKey, string status, string evidenceToken, CancellationToken cancellationToken = default)
    {
        ValidateConnectionKey(source, presenceKey);
        ValidateReviewStatus(status, allowConfirmed: false);
        meetingId = MeetingIdNormalizer.Normalize(meetingId);
        return WithMeetingLockAsync(meetingId, async () =>
        {
            var board = await BuildBoardAsync(meetingId, cancellationToken);
            var connection = RequireCurrentConnection(board, source, presenceKey);
            RequireEvidence(evidenceToken, connection.EvidenceToken);
            if (connection.MatchedRosterPersonId is not null) { throw new InvalidOperationException("명단에 연결된 참가자는 명단의 검토 항목을 사용하세요."); }
            await _repository.SaveMeetingReviewAsync(meetingId,
                new(AttendanceBoardProjector.ConnectionSubject(source, presenceKey), "connection", status, evidenceToken, DateTimeOffset.UtcNow), cancellationToken);
            return await BuildBoardAsync(meetingId, cancellationToken);
        }, cancellationToken);
    }

    private async Task RequireRosterPersonAsync(string personId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(personId)) { throw new ArgumentException("명단 인물을 선택하세요."); }
        var roster = await _repository.GetRosterPeopleAsync(cancellationToken);
        if (!roster.Any(person => person.Id == personId)) { throw new KeyNotFoundException("명단에서 해당 참가자를 찾을 수 없습니다."); }
    }

    private static CurrentParticipantConnection RequireCurrentConnection(AttendanceBoard board, string source, string presenceKey)
        => board.CurrentConnections?.SingleOrDefault(connection => connection.Source == source && connection.PresenceKey == presenceKey)
            ?? throw new InvalidOperationException("이 연결은 더 이상 현재 참가자 목록에 없습니다. 현황을 다시 확인하세요.");

    private static void ValidateConnectionKey(string source, string presenceKey)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(presenceKey) || source.Length > 200 || presenceKey.Length > 1000)
        {
            throw new ArgumentException("참가자 연결 정보가 올바르지 않습니다.");
        }
    }

    private static void ValidateReviewStatus(string status, bool allowConfirmed)
    {
        if (status is "pending" or "deferred" || (allowConfirmed && status == "confirmed")) { return; }
        throw new ArgumentException("검토 상태가 올바르지 않습니다.");
    }

    private static void RequireEvidence(string expected, string actual)
    {
        if (string.IsNullOrEmpty(expected) || !string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("참가자 정보가 변경되었습니다. 현황을 다시 읽고 확인하세요.");
        }
    }
}
