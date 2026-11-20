using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;

namespace ZoomCheck.Infrastructure.Services;

/// <summary>
/// Projects an <see cref="AttendanceBoard"/> from pre-fetched snapshot/presence state,
/// materialized event summaries, recent events, and meeting-scoped review decisions
/// without scanning the full event history.
/// </summary>
internal static class AttendanceBoardProjector
{
    private const string ZoomLiveSnapshotSource = "zoom-live-participants";

    private sealed record ManualAttendanceEvidence(
        string PersonId,
        DateTimeOffset ObservedAt,
        DateTimeOffset? LeftAt,
        bool HasRecordedJoin);

    public static AttendanceBoard Project(
        string meetingId,
        string attendanceDate,
        BoardProjectionData data,
        AttendanceMatchIndex? matchIndex = null)
    {
        var index = matchIndex ?? new AttendanceMatchIndex(data.Roster, data.Aliases);
        var reviewsByKey = BuildReviewIndex(data.Reviews);
        var matchesByConnection = data.MeetingMatches.ToLookup(
            match => (match.Source, match.PresenceKey));

        var personSummaries = data.PersonSummaries;
        var nameSummaries = data.NameSummaries;
        var recentEvents = data.RecentEvents;
        IReadOnlyList<ManualAttendanceEvidence> manualAttendance = Array.Empty<ManualAttendanceEvidence>();

        if (data.MeetingMatches.Count > 0)
        {
            ApplyMeetingMatchesToProjection(
                data,
                index,
                matchesByConnection,
                ref personSummaries,
                ref nameSummaries,
                ref recentEvents,
                out manualAttendance);
        }

        var zoomSnapshot = data.SnapshotSources.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Source, ZoomLiveSnapshotSource, StringComparison.Ordinal));
        var authoritativeSnapshots = zoomSnapshot is null ? data.SnapshotSources : new[] { zoomSnapshot };

        var currentCandidates = new (ParticipantSnapshotEntry Entry, string Source, MatchCandidate Candidate, bool ManualMatch)[data.CurrentPresence.Count];
        var currentConnections = new CurrentParticipantConnection[data.CurrentPresence.Count];
        for (var i = 0; i < data.CurrentPresence.Count; i++)
        {
            var (source, entry) = data.CurrentPresence[i];
            var candidate = MatchCurrentConnection(source, entry, index, matchesByConnection, out var manualMatch);
            currentCandidates[i] = (entry, source, candidate, manualMatch);

            var presenceKey = entry.PresenceKey ?? entry.NormalizedName;
            var connection = new CurrentParticipantConnection(
                PresenceKey: presenceKey,
                Source: source,
                RawName: entry.EffectiveRawDisplayName,
                DisplayName: entry.DisplayName,
                CanonicalName: entry.CanonicalName,
                NormalizedName: entry.NormalizedName,
                Email: entry.ParticipantEmail,
                FirstSeenAt: entry.FirstSeenAt,
                LastSeenAt: entry.LastSeenAt,
                MatchedRosterPersonId: candidate.Person?.Id,
                MatchedRosterPersonName: candidate.Person?.Name,
                Confidence: candidate.Confidence,
                MatchScore: candidate.Score,
                MatchReason: candidate.Reason,
                ManualMatch: manualMatch);
            currentConnections[i] = ApplyConnectionReview(connection, reviewsByKey);
        }

        Array.Sort(currentConnections, (left, right) => left.FirstSeenAt.CompareTo(right.FirstSeenAt));

        // A roster person matched by several live connections is still present exactly once.
        // The extra connections surface as review metadata instead of cancelling attendance.
        var connectionsByPerson = currentConnections
            .Where(connection => connection.MatchedRosterPersonId is not null)
            .GroupBy(connection => connection.MatchedRosterPersonId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var duplicateConnectionGroups = connectionsByPerson
            .Where(pair => pair.Value.Length > 1 && !data.Exclusions.Contains(pair.Key))
            .Select(pair => new DuplicateConnectionGroup(
                RosterPersonId: pair.Key,
                RosterPersonName: pair.Value[0].MatchedRosterPersonName ?? string.Empty,
                ConnectionCount: pair.Value.Length,
                Connections: pair.Value))
            .OrderByDescending(group => group.ConnectionCount)
            .ThenBy(group => group.RosterPersonId, StringComparer.Ordinal)
            .ToArray();

        var observationsByPerson = manualAttendance.Count > 0
            ? manualAttendance.ToLookup(evidence => evidence.PersonId, StringComparer.Ordinal)
            : null;
        var hasSnapshots = data.SnapshotSources.Count > 0;

        var people = new BoardPersonStatus[data.Roster.Count];
        for (var i = 0; i < data.Roster.Count; i++)
        {
            var person = data.Roster[i];
            personSummaries.TryGetValue(person.Id, out var summary);
            var observations = observationsByPerson is null
                ? Array.Empty<ManualAttendanceEvidence>()
                : observationsByPerson[person.Id].ToArray();
            var liveConnections = connectionsByPerson.GetValueOrDefault(person.Id)
                ?? Array.Empty<CurrentParticipantConnection>();

            var status = BuildPersonStatus(person, summary, observations, liveConnections, hasSnapshots);
            people[i] = ApplyPersonReview(status, liveConnections, reviewsByKey, data.Exclusions);
        }

        people = people.OrderBy(item => ParseSequence(item.Sequence)).ToArray();

        var unmatched = !hasSnapshots
            ? nameSummaries.Values
                .Where(summary => summary.UnmatchedEventCount > 0 && summary.UnmatchedLatestOccurredAt.HasValue)
                .Select(summary => new UnmatchedParticipantStatus(
                    ParticipantName: summary.UnmatchedLatestName ?? summary.NormalizedParticipantName,
                    AttendanceState: ResolveState(summary.UnmatchedLatestEventType),
                    LastSeenAt: summary.UnmatchedLatestOccurredAt!.Value,
                    EventCount: summary.UnmatchedEventCount))
                .OrderByDescending(item => item.LastSeenAt)
                .ToArray()
            : currentCandidates
                .Where(item => item.Candidate.Person is null)
                .Select(item =>
                {
                    var eventCount = nameSummaries.TryGetValue(item.Entry.NormalizedName, out var summary)
                        ? summary.TotalEventCount
                        : 0;
                    return new UnmatchedParticipantStatus(
                        ParticipantName: item.Entry.DisplayName,
                        AttendanceState: AttendanceState.Present,
                        LastSeenAt: item.Entry.LastSeenAt,
                        EventCount: eventCount);
                })
                .OrderByDescending(item => item.LastSeenAt)
                .ToArray();

        var confidenceCounts = new Dictionary<MatchConfidence, int>();
        foreach (var person in people)
        {
            if (person.IsExcluded || person.AttendanceState == AttendanceState.NotJoined)
            {
                continue;
            }

            confidenceCounts[person.Confidence] = confidenceCounts.TryGetValue(person.Confidence, out var count)
                ? count + 1
                : 1;
        }

        // Distinct groups in roster order so the dashboard can offer a filter without
        // re-deriving or re-sorting them. Ungrouped people contribute nothing.
        var groups = people
            .Select(person => person.Group)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        DateTimeOffset? lastReceivedAt = null;
        foreach (var snapshot in authoritativeSnapshots)
        {
            if (lastReceivedAt is null || snapshot.CapturedAt > lastReceivedAt.Value)
            {
                lastReceivedAt = snapshot.CapturedAt;
            }
        }

        var latestEventAt = recentEvents.Count > 0 ? recentEvents[0].OccurredAt : (DateTimeOffset?)null;

        return new AttendanceBoard(
            MeetingId: meetingId,
            GeneratedAt: DateTimeOffset.UtcNow,
            People: people,
            UnmatchedParticipants: unmatched,
            RecentEvents: recentEvents,
            ConfidenceCounts: confidenceCounts,
            CurrentConnections: currentConnections,
            DuplicateConnectionGroups: duplicateConnectionGroups,
            Groups: groups,
            SnapshotSources: authoritativeSnapshots.ToArray(),
            LastReceivedAt: lastReceivedAt,
            LatestEventAt: latestEventAt,
            AttendanceDate: attendanceDate);
    }

    private static void ApplyMeetingMatchesToProjection(
        BoardProjectionData data,
        AttendanceMatchIndex index,
        ILookup<(string Source, string PresenceKey), MeetingConnectionMatch> matchesByConnection,
        ref IReadOnlyDictionary<string, PersonEventSummary> personSummaries,
        ref IReadOnlyDictionary<string, NameEventSummary> nameSummaries,
        ref IReadOnlyList<ParticipantEvent> recentEvents,
        out IReadOnlyList<ManualAttendanceEvidence> attendanceEvidence)
    {
        var sessions = new Dictionary<(string Source, string Key), DateTimeOffset>();
        var attributedJoins = new HashSet<(string Source, string Key, DateTimeOffset Start, string PersonId)>();
        var departures = new Dictionary<(string Source, string Key, DateTimeOffset Start), DateTimeOffset>();
        var rewrittenByEventId = new Dictionary<string, ParticipantEvent>(StringComparer.Ordinal);
        var affectedPersonIds = new HashSet<string>(StringComparer.Ordinal);
        var affectedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var evt in data.MatchedConnectionEvents.OrderBy(evt => evt.OccurredAt).ThenBy(evt => evt.EventType))
        {
            var key = (evt.Source, evt.PresenceKey ?? evt.NormalizedParticipantName);
            if (evt.EventType == ParticipantEventType.Joined)
            {
                sessions.TryAdd(key, evt.OccurredAt);
            }

            MeetingConnectionMatch? match = null;
            if (sessions.TryGetValue(key, out var firstSeen))
            {
                foreach (var candidate in matchesByConnection[key])
                {
                    if (MatchesObservation(candidate, evt.EffectiveRawParticipantName, evt.ParticipantEmail, firstSeen))
                    {
                        match = candidate;
                        break;
                    }
                }
            }

            RosterPerson? person = null;
            if (match is not null)
            {
                index.TryGetPersonById(match.RosterPersonId, out person);
            }

            var projectedEvent = person is null
                ? evt
                : evt with { MatchedRosterPersonId = person.Id, Confidence = MatchConfidence.Verified };

            if (person is not null)
            {
                rewrittenByEventId[evt.Id] = projectedEvent;
                if (!string.IsNullOrWhiteSpace(evt.MatchedRosterPersonId))
                {
                    affectedPersonIds.Add(evt.MatchedRosterPersonId);
                }
                affectedPersonIds.Add(person.Id);
                affectedNames.Add(evt.NormalizedParticipantName);
            }

            if (projectedEvent.MatchedRosterPersonId is { } joinedPersonId && evt.EventType == ParticipantEventType.Joined)
            {
                attributedJoins.Add((key.Source, key.Item2, sessions[key], joinedPersonId));
            }

            if (evt.EventType == ParticipantEventType.Left)
            {
                if (sessions.TryGetValue(key, out var start))
                {
                    departures[(key.Source, key.Item2, start)] = evt.OccurredAt;
                }
                sessions.Remove(key);
            }
        }

        // An accepted live observation proves attendance even if its original Join carried
        // a different name/email. Keep that evidence separate from the captured event log.
        attendanceEvidence = data.MeetingMatches
            .Where(match => index.TryGetPersonById(match.RosterPersonId, out _))
            .GroupBy(match => (match.Source, match.PresenceKey, match.FirstSeenAt, match.RosterPersonId))
            .Select(group => new ManualAttendanceEvidence(
                group.Key.RosterPersonId,
                group.Min(match => match.ObservedAt),
                departures.TryGetValue((group.Key.Source, group.Key.PresenceKey, group.Key.FirstSeenAt), out var leftAt) ? leftAt : null,
                attributedJoins.Contains((group.Key.Source, group.Key.PresenceKey, group.Key.FirstSeenAt, group.Key.RosterPersonId))))
            .ToArray();

        if (rewrittenByEventId.Count == 0)
        {
            return;
        }

        var updatedRecent = new ParticipantEvent[recentEvents.Count];
        for (var i = 0; i < recentEvents.Count; i++)
        {
            var evt = recentEvents[i];
            updatedRecent[i] = rewrittenByEventId.TryGetValue(evt.Id, out var rewritten) ? rewritten : evt;
        }
        recentEvents = updatedRecent;

        var mergedPersonEvents = new Dictionary<string, List<ParticipantEvent>>(StringComparer.Ordinal);
        var seenPersonEventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawEvt in data.AffectedPersonEvents.Concat(data.MatchedConnectionEvents))
        {
            if (!seenPersonEventIds.Add(rawEvt.Id))
            {
                continue;
            }

            var projected = rewrittenByEventId.TryGetValue(rawEvt.Id, out var rewritten) ? rewritten : rawEvt;
            if (!string.IsNullOrWhiteSpace(projected.MatchedRosterPersonId)
                && affectedPersonIds.Contains(projected.MatchedRosterPersonId))
            {
                if (!mergedPersonEvents.TryGetValue(projected.MatchedRosterPersonId, out var list))
                {
                    list = new List<ParticipantEvent>();
                    mergedPersonEvents[projected.MatchedRosterPersonId] = list;
                }
                list.Add(projected);
            }
        }

        var updatedPersonSummaries = new Dictionary<string, PersonEventSummary>(personSummaries, StringComparer.Ordinal);
        foreach (var personId in affectedPersonIds)
        {
            if (mergedPersonEvents.TryGetValue(personId, out var events) && events.Count > 0)
            {
                updatedPersonSummaries[personId] = SummarizePersonEvents(personId, events);
            }
            else
            {
                updatedPersonSummaries.Remove(personId);
            }
        }
        personSummaries = updatedPersonSummaries;

        if (data.SnapshotSources.Count == 0 && affectedNames.Count > 0)
        {
            var mergedNameEvents = new Dictionary<string, List<ParticipantEvent>>(StringComparer.Ordinal);
            var seenNameEventIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rawEvt in data.AffectedNameEvents.Concat(data.MatchedConnectionEvents))
            {
                if (!seenNameEventIds.Add(rawEvt.Id))
                {
                    continue;
                }

                var projected = rewrittenByEventId.TryGetValue(rawEvt.Id, out var rewritten) ? rewritten : rawEvt;
                if (affectedNames.Contains(projected.NormalizedParticipantName))
                {
                    if (!mergedNameEvents.TryGetValue(projected.NormalizedParticipantName, out var list))
                    {
                        list = new List<ParticipantEvent>();
                        mergedNameEvents[projected.NormalizedParticipantName] = list;
                    }
                    list.Add(projected);
                }
            }

            var updatedNameSummaries = new Dictionary<string, NameEventSummary>(nameSummaries, StringComparer.Ordinal);
            foreach (var name in affectedNames)
            {
                if (mergedNameEvents.TryGetValue(name, out var events) && events.Count > 0)
                {
                    updatedNameSummaries[name] = SummarizeNameEvents(name, events);
                }
                else
                {
                    updatedNameSummaries.Remove(name);
                }
            }
            nameSummaries = updatedNameSummaries;
        }
    }

    private static PersonEventSummary SummarizePersonEvents(string personId, List<ParticipantEvent> events)
    {
        var ordered = events.OrderBy(evt => evt.OccurredAt).ToList();
        var joinCount = 0;
        DateTimeOffset? lastJoinedAt = null;
        DateTimeOffset? lastLeftAt = null;
        ParticipantEventType? lastNonRename = null;
        var maxConfidence = MatchConfidence.Unmatched;
        var seenConfidences = new List<MatchConfidence>();

        foreach (var evt in ordered)
        {
            if (evt.EventType == ParticipantEventType.Joined)
            {
                joinCount++;
                if (lastJoinedAt is null || evt.OccurredAt > lastJoinedAt.Value)
                {
                    lastJoinedAt = evt.OccurredAt;
                }
            }
            else if (evt.EventType == ParticipantEventType.Left)
            {
                if (lastLeftAt is null || evt.OccurredAt > lastLeftAt.Value)
                {
                    lastLeftAt = evt.OccurredAt;
                }
            }

            if (evt.EventType != ParticipantEventType.NameChanged)
            {
                lastNonRename = evt.EventType;
            }

            if (evt.Confidence > maxConfidence)
            {
                maxConfidence = evt.Confidence;
            }

            if (!seenConfidences.Contains(evt.Confidence))
            {
                seenConfidences.Add(evt.Confidence);
            }
        }

        return new PersonEventSummary(
            RosterPersonId: personId,
            EventCount: ordered.Count,
            JoinCount: joinCount,
            LastJoinedAt: lastJoinedAt,
            LastLeftAt: lastLeftAt,
            LastNonRenameEventType: lastNonRename,
            MaxConfidence: maxConfidence,
            ConfidenceOrder: string.Join(", ", seenConfidences));
    }

    private static NameEventSummary SummarizeNameEvents(string normalizedName, List<ParticipantEvent> events)
    {
        var ordered = events.OrderBy(evt => evt.OccurredAt).ToList();
        var unmatchedCount = 0;
        ParticipantEvent? latestUnmatched = null;

        foreach (var evt in ordered)
        {
            if (string.IsNullOrWhiteSpace(evt.MatchedRosterPersonId))
            {
                unmatchedCount++;
                latestUnmatched = evt;
            }
        }

        return new NameEventSummary(
            NormalizedParticipantName: normalizedName,
            TotalEventCount: ordered.Count,
            UnmatchedEventCount: unmatchedCount,
            UnmatchedLatestName: latestUnmatched?.ParticipantName,
            UnmatchedLatestEventType: latestUnmatched?.EventType,
            UnmatchedLatestOccurredAt: latestUnmatched?.OccurredAt);
    }

    private static BoardPersonStatus BuildPersonStatus(
        RosterPerson person,
        PersonEventSummary? summary,
        ManualAttendanceEvidence[] observations,
        CurrentParticipantConnection[] liveConnections,
        bool hasSnapshots)
    {
        var joinCount = summary?.JoinCount ?? 0;
        var lastJoin = summary?.LastJoinedAt;
        var lastLeft = summary?.LastLeftAt;

        foreach (var observation in observations)
        {
            if (!observation.HasRecordedJoin)
            {
                joinCount++;
                if (lastJoin is null || observation.ObservedAt > lastJoin.Value)
                {
                    lastJoin = observation.ObservedAt;
                }
            }

            if (observation.LeftAt.HasValue && (lastLeft is null || observation.LeftAt.Value > lastLeft.Value))
            {
                lastLeft = observation.LeftAt.Value;
            }
        }

        var hasEvents = summary is { EventCount: > 0 };
        var confidence = hasEvents ? summary!.MaxConfidence : MatchConfidence.Unmatched;
        var reason = hasEvents ? summary!.ConfidenceOrder : "No event yet";
        if (!hasEvents && observations.Length > 0)
        {
            confidence = MatchConfidence.Verified;
            reason = "이번 회의에서 운영자가 출석을 확인했습니다.";
        }

        if (liveConnections.Length > 0)
        {
            var maxLiveConfidence = liveConnections[0].Confidence;
            var minFirstSeen = liveConnections[0].FirstSeenAt;
            for (var i = 1; i < liveConnections.Length; i++)
            {
                if (liveConnections[i].Confidence > maxLiveConfidence)
                {
                    maxLiveConfidence = liveConnections[i].Confidence;
                }
                if (liveConnections[i].FirstSeenAt < minFirstSeen)
                {
                    minFirstSeen = liveConnections[i].FirstSeenAt;
                }
            }

            confidence = maxLiveConfidence;
            reason = liveConnections.Length == 1
                ? liveConnections[0].MatchReason
                : string.Join(" · ", liveConnections.Select(connection => connection.MatchReason).Distinct());
            lastJoin ??= minFirstSeen;
        }

        var attendanceState = !hasSnapshots
            ? ResolveState(summary?.LastNonRenameEventType)
            : liveConnections.Length > 0
                ? AttendanceState.Present
                : joinCount > 0
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
            JoinCount: joinCount,
            ActiveConnectionCount: liveConnections.Length,
            HasDuplicateConnections: liveConnections.Length > 1);
    }

    private static MatchCandidate MatchCurrentConnection(
        string source,
        ParticipantSnapshotEntry entry,
        AttendanceMatchIndex index,
        ILookup<(string Source, string PresenceKey), MeetingConnectionMatch> matchesByConnection,
        out bool manuallyMatched)
    {
        var presenceKey = entry.PresenceKey ?? entry.NormalizedName;
        MeetingConnectionMatch? manual = null;
        foreach (var candidate in matchesByConnection[(source, presenceKey)])
        {
            if (MatchesObservation(candidate, entry.EffectiveRawDisplayName, entry.ParticipantEmail, entry.FirstSeenAt))
            {
                manual = candidate;
                break;
            }
        }

        RosterPerson? person = null;
        if (manual is not null)
        {
            index.TryGetPersonById(manual.RosterPersonId, out person);
        }

        manuallyMatched = person is not null;
        return person is null
            ? index.MatchParticipant(entry.EffectiveRawDisplayName, entry.DisplayName, entry.ParticipantEmail)
            : new MatchCandidate(person, MatchConfidence.Verified, 1, "이번 회의에서 운영자가 연결했습니다.");
    }

    private static Dictionary<(string SubjectKey, string Kind), MeetingReviewDecision> BuildReviewIndex(
        IReadOnlyList<MeetingReviewDecision> decisions)
    {
        var map = new Dictionary<(string SubjectKey, string Kind), MeetingReviewDecision>(decisions.Count);
        foreach (var decision in decisions)
        {
            map.TryAdd((decision.SubjectKey, decision.Kind), decision);
        }
        return map;
    }

    private static string ReviewStatus(
        IReadOnlyDictionary<(string SubjectKey, string Kind), MeetingReviewDecision> decisions,
        string subject,
        string kind,
        string token,
        bool required)
    {
        if (!required)
        {
            return "none";
        }

        return decisions.TryGetValue((subject, kind), out var decision)
            && string.Equals(decision.EvidenceToken, token, StringComparison.Ordinal)
                ? decision.Status
                : "pending";
    }

    private static CurrentParticipantConnection ApplyConnectionReview(
        CurrentParticipantConnection connection,
        IReadOnlyDictionary<(string SubjectKey, string Kind), MeetingReviewDecision> decisions)
    {
        var token = Evidence(new
        {
            connection.Source,
            connection.PresenceKey,
            connection.RawName,
            Email = NormalizeEmail(connection.Email),
            connection.FirstSeenAt,
            connection.MatchedRosterPersonId,
            connection.Confidence,
            connection.ManualMatch
        });
        return connection with
        {
            EvidenceToken = token,
            ReviewStatus = ReviewStatus(
                decisions,
                ConnectionSubject(connection.Source, connection.PresenceKey),
                "connection",
                token,
                connection.MatchedRosterPersonId is null)
        };
    }

    private static BoardPersonStatus ApplyPersonReview(
        BoardPersonStatus person,
        CurrentParticipantConnection[] connections,
        IReadOnlyDictionary<(string SubjectKey, string Kind), MeetingReviewDecision> decisions,
        IReadOnlySet<string> excluded)
    {
        var connectionEvidence = connections.Length == 0
            ? Array.Empty<string>()
            : connections.Length == 1
                ? new[] { connections[0].EvidenceToken }
                : connections.Select(connection => connection.EvidenceToken).OrderBy(token => token, StringComparer.Ordinal).ToArray();
        var identityToken = Evidence(new
        {
            person.RosterPersonId,
            person.Confidence,
            person.LastJoinedAt,
            person.LastLeftAt,
            Connections = connectionEvidence
        });
        var duplicateToken = Evidence(new
        {
            person.RosterPersonId,
            Connections = connectionEvidence
        });
        var identity = ReviewStatus(
            decisions,
            person.RosterPersonId,
            "identity",
            identityToken,
            person.AttendanceState != AttendanceState.NotJoined
                && person.Confidence is MatchConfidence.NameOnly or MatchConfidence.Possible or MatchConfidence.Unmatched);
        var duplicate = ReviewStatus(
            decisions,
            person.RosterPersonId,
            "duplicate",
            duplicateToken,
            connections.Length > 1);
        var isExcluded = excluded.Contains(person.RosterPersonId);
        return person with
        {
            IsExcluded = isExcluded,
            IdentityReviewStatus = identity,
            DuplicateReviewStatus = duplicate,
            ReviewRequired = !isExcluded && (identity is "pending" or "deferred" || duplicate is "pending" or "deferred"),
            IdentityEvidenceToken = identityToken,
            DuplicateEvidenceToken = duplicateToken
        };
    }

    private static bool MatchesObservation(
        MeetingConnectionMatch match,
        string rawName,
        string? email,
        DateTimeOffset firstSeenAt)
        => match.FirstSeenAt == firstSeenAt
            && match.ObservedName == rawName
            && match.ObservedEmail == NormalizeEmail(email);

    internal static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    internal static string ConnectionSubject(string source, string presenceKey)
        => JsonSerializer.Serialize(new[] { source, presenceKey });

    private static string Evidence(object value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private static AttendanceState ResolveState(ParticipantEventType? eventType)
    {
        if (eventType is null)
        {
            return AttendanceState.NotJoined;
        }

        return eventType.Value switch
        {
            ParticipantEventType.Joined => AttendanceState.Present,
            // A rename does not change whether the participant is in the meeting.
            ParticipantEventType.NameChanged => AttendanceState.Present,
            _ => AttendanceState.Left
        };
    }

    private static int ParseSequence(string value) => int.TryParse(value, out var parsed) ? parsed : int.MaxValue;
}
