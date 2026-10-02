using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Services;

public sealed partial class AttendanceApplicationService
{
    public async Task<AttendanceBoard> BuildBoardAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        meetingId = MeetingIdNormalizer.Normalize(meetingId);
        var roster = await _repository.GetRosterPeopleAsync(cancellationToken);
        var events = await _repository.GetParticipantEventsAsync(meetingId, cancellationToken);
        var aliases = await _repository.GetAliasMapAsync(cancellationToken);
        var snapshots = await _repository.GetParticipantSnapshotSourcesAsync(meetingId, cancellationToken);
        var attendanceDate = CurrentAttendanceDate();
        var exclusions = await _repository.GetMeetingExclusionsAsync(meetingId, attendanceDate, cancellationToken);
        var reviews = await _repository.GetMeetingReviewsAsync(meetingId, cancellationToken);
        var meetingMatches = await _repository.GetMeetingConnectionMatchesAsync(meetingId, cancellationToken);
        events = ApplyMeetingMatchesToEvents(events, meetingMatches, roster, out var manualAttendance);
        var zoomSnapshot = snapshots.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Source, "zoom-live-participants", StringComparison.Ordinal));
        var authoritativeSnapshots = zoomSnapshot is null ? snapshots : new[] { zoomSnapshot };
        var currentPresence = new List<(string Source, ParticipantSnapshotEntry Entry)>();
        foreach (var snapshot in authoritativeSnapshots)
        {
            var entries = await _repository.GetParticipantPresenceAsync(meetingId, snapshot.Source, cancellationToken);
            currentPresence.AddRange(entries.Select(entry => (snapshot.Source, entry)));
        }

        var currentCandidates = currentPresence
            .Select(item =>
            {
                var candidate = MatchCurrentConnection(item.Source, item.Entry, roster, aliases, meetingMatches, out var manualMatch);
                return new { item.Entry, item.Source, Candidate = candidate, ManualMatch = manualMatch };
            })
            .ToArray();
        var currentConnections = currentCandidates
            .Select(item =>
            {
                var presenceKey = item.Entry.PresenceKey ?? item.Entry.NormalizedName;
                return new CurrentParticipantConnection(
                    PresenceKey: presenceKey,
                    Source: item.Source,
                    RawName: item.Entry.EffectiveRawDisplayName,
                    DisplayName: item.Entry.DisplayName,
                    CanonicalName: item.Entry.CanonicalName,
                    NormalizedName: item.Entry.NormalizedName,
                    Email: item.Entry.ParticipantEmail,
                    FirstSeenAt: item.Entry.FirstSeenAt,
                    LastSeenAt: item.Entry.LastSeenAt,
                    MatchedRosterPersonId: item.Candidate.Person?.Id,
                    MatchedRosterPersonName: item.Candidate.Person?.Name,
                    Confidence: item.Candidate.Confidence,
                    MatchScore: item.Candidate.Score,
                    MatchReason: item.Candidate.Reason,
                    ManualMatch: item.ManualMatch);
            })
            .Select(connection => ApplyConnectionReview(connection, reviews))
            .OrderBy(connection => connection.FirstSeenAt)
            .ToArray();

        // A roster person matched by several live connections is still present exactly once.
        // The extra connections surface as review metadata instead of cancelling attendance.
        var connectionsByPerson = currentConnections
            .Where(connection => connection.MatchedRosterPersonId is not null)
            .GroupBy(connection => connection.MatchedRosterPersonId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var duplicateConnectionGroups = connectionsByPerson
            .Where(pair => pair.Value.Length > 1 && !exclusions.Contains(pair.Key))
            .Select(pair => new DuplicateConnectionGroup(
                RosterPersonId: pair.Key,
                RosterPersonName: pair.Value[0].MatchedRosterPersonName ?? string.Empty,
                ConnectionCount: pair.Value.Length,
                Connections: pair.Value))
            .OrderByDescending(group => group.ConnectionCount)
            .ThenBy(group => group.RosterPersonId, StringComparer.Ordinal)
            .ToArray();

        var groupedByRosterPerson = events
            .Where(evt => !string.IsNullOrWhiteSpace(evt.MatchedRosterPersonId))
            .GroupBy(evt => evt.MatchedRosterPersonId!)
            .ToDictionary(group => group.Key, group => group.OrderBy(evt => evt.OccurredAt).ToList(), StringComparer.Ordinal);

        var observationsByPerson = manualAttendance.ToLookup(evidence => evidence.PersonId, StringComparer.Ordinal);
        var people = roster.Select(person =>
        {
            var personEvents = groupedByRosterPerson.GetValueOrDefault(person.Id) ?? new List<ParticipantEvent>();
            var liveConnections = connectionsByPerson.GetValueOrDefault(person.Id) ?? Array.Empty<CurrentParticipantConnection>();
            var status = BuildPersonStatus(person, personEvents, observationsByPerson[person.Id].ToArray(),
                liveConnections, hasSnapshots: snapshots.Count > 0);
            return ApplyPersonReview(status, liveConnections, reviews, exclusions);
        }).OrderBy(item => ParseSequence(item.Sequence)).ToArray();

        var eventsByName = events.ToLookup(evt => evt.NormalizedParticipantName, StringComparer.Ordinal);
        var unmatched = snapshots.Count == 0
            ? events
                .Where(evt => string.IsNullOrWhiteSpace(evt.MatchedRosterPersonId))
                .GroupBy(evt => evt.NormalizedParticipantName)
                .Select(group =>
                {
                    var latest = group.OrderBy(evt => evt.OccurredAt).Last();
                    return new UnmatchedParticipantStatus(
                        ParticipantName: latest.ParticipantName,
                        AttendanceState: ResolveState(latest),
                        LastSeenAt: latest.OccurredAt,
                        EventCount: group.Count());
                })
                .OrderByDescending(item => item.LastSeenAt)
                .ToArray()
            : currentCandidates
                .Where(item => item.Candidate.Person is null)
                .Select(item =>
                {
                    var eventCount = eventsByName[item.Entry.NormalizedName].Count();
                    return new UnmatchedParticipantStatus(
                        ParticipantName: item.Entry.DisplayName,
                        AttendanceState: AttendanceState.Present,
                        LastSeenAt: item.Entry.LastSeenAt,
                        EventCount: eventCount);
                })
                .OrderByDescending(item => item.LastSeenAt)
                .ToArray();

        var confidenceCounts = people
            .Where(person => !person.IsExcluded && person.AttendanceState != AttendanceState.NotJoined)
            .GroupBy(person => person.Confidence)
            .ToDictionary(group => group.Key, group => group.Count());

        // Distinct groups in roster order so the dashboard can offer a filter without
        // re-deriving or re-sorting them. Ungrouped people contribute nothing.
        var groups = people
            .Select(person => person.Group)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new AttendanceBoard(
            MeetingId: meetingId,
            GeneratedAt: DateTimeOffset.UtcNow,
            People: people,
            UnmatchedParticipants: unmatched,
            RecentEvents: events.OrderByDescending(evt => evt.OccurredAt).Take(30).ToArray(),
            ConfidenceCounts: confidenceCounts,
            CurrentConnections: currentConnections,
            DuplicateConnectionGroups: duplicateConnectionGroups,
            Groups: groups,
            SnapshotSources: authoritativeSnapshots.ToArray(),
            LastReceivedAt: authoritativeSnapshots.Select(snapshot => (DateTimeOffset?)snapshot.CapturedAt).Max(),
            LatestEventAt: events.Select(evt => (DateTimeOffset?)evt.OccurredAt).Max(),
            AttendanceDate: attendanceDate);
    }

    private static BoardPersonStatus BuildPersonStatus(
        RosterPerson person,
        IReadOnlyList<ParticipantEvent> personEvents,
        ManualAttendanceEvidence[] observations,
        CurrentParticipantConnection[] liveConnections,
        bool hasSnapshots)
    {
        var joined = personEvents.Where(evt => evt.EventType == ParticipantEventType.Joined).ToList();
        var joinTimes = joined.Select(evt => evt.OccurredAt)
            .Concat(observations.Where(evidence => !evidence.HasRecordedJoin).Select(evidence => evidence.ObservedAt)).ToArray();
        var lastEvent = personEvents.LastOrDefault(evt => evt.EventType != ParticipantEventType.NameChanged);
        var lastJoin = joinTimes.Select(time => (DateTimeOffset?)time).Max();
        var lastLeft = personEvents.Where(evt => evt.EventType == ParticipantEventType.Left).Select(evt => (DateTimeOffset?)evt.OccurredAt)
            .Concat(observations.Select(evidence => evidence.LeftAt)).Max();
        var confidence = personEvents.Any() ? personEvents.MaxBy(evt => evt.Confidence)?.Confidence ?? MatchConfidence.Unmatched : MatchConfidence.Unmatched;
        var reason = personEvents.Any() ? string.Join(", ", personEvents.Select(evt => evt.Confidence).Distinct()) : "No event yet";
        if (!personEvents.Any() && observations.Length > 0) { confidence = MatchConfidence.Verified; reason = "이번 회의에서 운영자가 출석을 확인했습니다."; }
        if (liveConnections.Length > 0)
        {
            confidence = liveConnections.MaxBy(connection => connection.Confidence)!.Confidence;
            reason = string.Join(" · ", liveConnections.Select(connection => connection.MatchReason).Distinct());
            lastJoin ??= liveConnections.Min(connection => connection.FirstSeenAt);
        }
        var attendanceState = !hasSnapshots
            ? ResolveState(lastEvent)
            : liveConnections.Length > 0
                ? AttendanceState.Present
                : joinTimes.Length > 0
                    ? AttendanceState.Left
                    : AttendanceState.NotJoined;

        return new BoardPersonStatus(
            RosterPersonId: person.Id,
            Sequence: person.Sequence,
            Name: person.Name,
            Organization: person.Organization,
            Group: person.Group ?? string.Empty,
            AttendanceState: attendanceState,
            Confidence: confidence,
            ConfidenceReason: reason,
            LastJoinedAt: lastJoin,
            LastLeftAt: lastLeft,
            JoinCount: joinTimes.Length,
            ActiveConnectionCount: liveConnections.Length,
            HasDuplicateConnections: liveConnections.Length > 1);
    }

    private static AttendanceState ResolveState(ParticipantEvent? participantEvent)
    {
        if (participantEvent is null)
        {
            return AttendanceState.NotJoined;
        }

        return participantEvent.EventType switch
        {
            ParticipantEventType.Joined => AttendanceState.Present,
            // A rename does not change whether the participant is in the meeting.
            ParticipantEventType.NameChanged => AttendanceState.Present,
            _ => AttendanceState.Left
        };
    }

    private static int ParseSequence(string value) => int.TryParse(value, out var parsed) ? parsed : int.MaxValue;
}
