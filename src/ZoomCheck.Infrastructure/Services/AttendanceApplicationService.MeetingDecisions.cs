using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Services;

public sealed partial class AttendanceApplicationService
{
    private async Task<T> WithMeetingLockAsync<T>(string meetingId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = _snapshotLocks.GetOrAdd(meetingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { gate.Release(); }
    }

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
                NormalizeEmail(connection.Email), connection.FirstSeenAt, connection.LastSeenAt);
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
                new(ConnectionSubject(source, presenceKey), "connection", status, evidenceToken, DateTimeOffset.UtcNow), cancellationToken);
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

    private static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
    private static string Evidence(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static string ConnectionSubject(string source, string presenceKey) => JsonSerializer.Serialize(new[] { source, presenceKey });

    private static bool MatchesObservation(MeetingConnectionMatch match, string rawName, string? email, DateTimeOffset firstSeenAt)
        => match.FirstSeenAt == firstSeenAt && match.ObservedName == rawName && match.ObservedEmail == NormalizeEmail(email);

    private MatchCandidate MatchCurrentConnection(string source, ParticipantSnapshotEntry entry,
        IReadOnlyList<RosterPerson> roster, IReadOnlyDictionary<string, string> aliases, IReadOnlyList<MeetingConnectionMatch> matches,
        out bool manuallyMatched)
    {
        var manual = matches.FirstOrDefault(match => match.Source == source && match.PresenceKey == (entry.PresenceKey ?? entry.NormalizedName)
            && MatchesObservation(match, entry.EffectiveRawDisplayName, entry.ParticipantEmail, entry.FirstSeenAt));
        var person = manual is null ? null : roster.FirstOrDefault(person => person.Id == manual.RosterPersonId);
        manuallyMatched = person is not null;
        return person is null
            ? MatchParticipant(roster, aliases, entry.EffectiveRawDisplayName, entry.DisplayName, entry.ParticipantEmail)
            : new(person, MatchConfidence.Verified, 1, "이번 회의에서 운영자가 연결했습니다.");
    }

    // Project decisions onto the board; the original captured events are never rewritten.
    private sealed record ManualAttendanceEvidence(string PersonId, DateTimeOffset ObservedAt, DateTimeOffset? LeftAt, bool HasRecordedJoin);

    private static IReadOnlyList<ParticipantEvent> ApplyMeetingMatchesToEvents(IReadOnlyList<ParticipantEvent> events,
        IReadOnlyList<MeetingConnectionMatch> matches, IReadOnlyList<RosterPerson> roster, out IReadOnlyList<ManualAttendanceEvidence> attendanceEvidence)
    {
        var sessions = new Dictionary<(string Source, string Key), DateTimeOffset>();
        var projected = new List<ParticipantEvent>();
        var attributedJoins = new HashSet<(string Source, string Key, DateTimeOffset Start, string PersonId)>();
        var departures = new Dictionary<(string Source, string Key, DateTimeOffset Start), DateTimeOffset>();
        foreach (var evt in events.OrderBy(evt => evt.OccurredAt).ThenBy(evt => evt.EventType))
        {
            var key = (evt.Source, evt.PresenceKey ?? evt.NormalizedParticipantName);
            if (evt.EventType == ParticipantEventType.Joined) { sessions.TryAdd(key, evt.OccurredAt); }
            var match = matches.FirstOrDefault(match => match.Source == key.Source && match.PresenceKey == key.Item2
                && sessions.TryGetValue(key, out var firstSeen) && MatchesObservation(match, evt.EffectiveRawParticipantName, evt.ParticipantEmail, firstSeen));
            var person = match is null ? null : roster.FirstOrDefault(person => person.Id == match.RosterPersonId);
            var projectedEvent = person is null ? evt : evt with { MatchedRosterPersonId = person.Id, Confidence = MatchConfidence.Verified };
            projected.Add(projectedEvent);
            if (projectedEvent.MatchedRosterPersonId is { } joinedPersonId && evt.EventType == ParticipantEventType.Joined)
            {
                attributedJoins.Add((key.Source, key.Item2, sessions[key], joinedPersonId));
            }
            if (evt.EventType == ParticipantEventType.Left)
            {
                if (sessions.TryGetValue(key, out var start)) { departures[(key.Source, key.Item2, start)] = evt.OccurredAt; }
                sessions.Remove(key);
            }
        }
        // An accepted live observation proves attendance even if its original Join carried
        // a different name/email. Keep that evidence separate from the captured event log.
        attendanceEvidence = matches.Where(match => roster.Any(person => person.Id == match.RosterPersonId))
            .GroupBy(match => (match.Source, match.PresenceKey, match.FirstSeenAt, match.RosterPersonId))
            .Select(group => new ManualAttendanceEvidence(group.Key.RosterPersonId, group.Min(match => match.ObservedAt),
                departures.TryGetValue((group.Key.Source, group.Key.PresenceKey, group.Key.FirstSeenAt), out var leftAt) ? leftAt : null,
                attributedJoins.Contains((group.Key.Source, group.Key.PresenceKey, group.Key.FirstSeenAt, group.Key.RosterPersonId))))
            .ToArray();
        return projected;
    }

    private static string ReviewStatus(IReadOnlyList<MeetingReviewDecision> decisions, string subject, string kind, string token, bool required)
    {
        if (!required) { return "none"; }
        return decisions.FirstOrDefault(decision => decision.SubjectKey == subject && decision.Kind == kind && decision.EvidenceToken == token)?.Status ?? "pending";
    }

    private static CurrentParticipantConnection ApplyConnectionReview(CurrentParticipantConnection connection, IReadOnlyList<MeetingReviewDecision> decisions)
    {
        var token = Evidence(new { connection.Source, connection.PresenceKey, connection.RawName, Email = NormalizeEmail(connection.Email),
            connection.FirstSeenAt, connection.MatchedRosterPersonId, connection.Confidence, connection.ManualMatch });
        return connection with { EvidenceToken = token, ReviewStatus = ReviewStatus(decisions,
            ConnectionSubject(connection.Source, connection.PresenceKey), "connection", token, connection.MatchedRosterPersonId is null) };
    }

    private static BoardPersonStatus ApplyPersonReview(BoardPersonStatus person, CurrentParticipantConnection[] connections,
        IReadOnlyList<MeetingReviewDecision> decisions, IReadOnlySet<string> excluded)
    {
        var connectionEvidence = connections.Select(connection => connection.EvidenceToken).OrderBy(token => token, StringComparer.Ordinal).ToArray();
        var identityToken = Evidence(new { person.RosterPersonId, person.Confidence, person.LastJoinedAt, person.LastLeftAt, Connections = connectionEvidence });
        var duplicateToken = Evidence(new { person.RosterPersonId, Connections = connectionEvidence });
        var identity = ReviewStatus(decisions, person.RosterPersonId, "identity", identityToken,
            person.AttendanceState != AttendanceState.NotJoined && person.Confidence is MatchConfidence.NameOnly or MatchConfidence.Possible or MatchConfidence.Unmatched);
        var duplicate = ReviewStatus(decisions, person.RosterPersonId, "duplicate", duplicateToken, connections.Length > 1);
        var isExcluded = excluded.Contains(person.RosterPersonId);
        return person with { IsExcluded = isExcluded, IdentityReviewStatus = identity, DuplicateReviewStatus = duplicate,
            ReviewRequired = !isExcluded && (identity is "pending" or "deferred" || duplicate is "pending" or "deferred"),
            IdentityEvidenceToken = identityToken, DuplicateEvidenceToken = duplicateToken };
    }
}
