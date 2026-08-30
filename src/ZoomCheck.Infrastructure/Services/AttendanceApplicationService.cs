using System.Text.Json;
using System.Text;
using System.Collections.Concurrent;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;

namespace ZoomCheck.Infrastructure.Services;

public sealed class AttendanceApplicationService
{
    private readonly ExcelRosterParser _rosterParser;
    private readonly SqliteAttendanceRepository _repository;
    private readonly AttendanceMatcher _matcher;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _snapshotLocks = new(StringComparer.Ordinal);

    public AttendanceApplicationService(
        ExcelRosterParser rosterParser,
        SqliteAttendanceRepository repository,
        AttendanceMatcher matcher)
    {
        _rosterParser = rosterParser;
        _repository = repository;
        _matcher = matcher;
    }

    public async Task<RosterImportResult> ImportRosterAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var roster = _rosterParser.Parse(filePath);
        await _repository.ReplaceRosterAsync(roster, cancellationToken);
        return roster;
    }

    public async Task<RosterImportResult> ImportRosterAsync(
        Stream stream,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var roster = _rosterParser.Parse(stream, $"upload://{displayName}", displayName);
        await _repository.ReplaceRosterAsync(roster, cancellationToken);
        return roster;
    }

    public Task<IReadOnlyList<RosterPerson>> GetRosterAsync(CancellationToken cancellationToken = default)
        => _repository.GetRosterPeopleAsync(cancellationToken);

    public Task<IReadOnlyList<ParticipantEvent>> GetParticipantEventsForMeetingAsync(string meetingId, CancellationToken cancellationToken = default)
        => _repository.GetParticipantEventsAsync(meetingId, cancellationToken);

    public async Task<ParticipantEvent> RecordParticipantEventAsync(ParticipantEventInput input, CancellationToken cancellationToken = default)
    {
        var roster = await _repository.GetRosterPeopleAsync(cancellationToken);
        var aliases = await _repository.GetAliasMapAsync(cancellationToken);
        var candidate = _matcher.Match(roster, aliases, input.ParticipantName, input.ParticipantEmail);

        var participantEvent = new ParticipantEvent(
            Id: Guid.NewGuid().ToString("N"),
            MeetingId: input.MeetingId,
            OccurredAt: input.OccurredAt,
            EventType: input.EventType,
            ParticipantName: input.ParticipantName,
            NormalizedParticipantName: NameNormalizer.Normalize(input.ParticipantName),
            ParticipantEmail: input.ParticipantEmail,
            Confidence: candidate.Confidence,
            MatchedRosterPersonId: candidate.Person?.Id,
            Source: input.Source,
            RawPayload: input.RawPayload);

        await _repository.AppendParticipantEventAsync(participantEvent, cancellationToken);
        return participantEvent;
    }

    public Task<ParticipantEvent> RecordZoomEventAsync(ZoomParticipantEventInput input, CancellationToken cancellationToken = default)
        => RecordParticipantEventAsync(input, cancellationToken);

    /// <summary>
    /// Applies a full "currently present" participant list for a meeting and capture source.
    /// Names not seen before produce Joined events, names missing from the snapshot but present
    /// in that source's previous snapshot produce Left events. Presence is scoped by
    /// (meeting, source) so a snapshot never marks participants observed elsewhere as left.
    /// </summary>
    public async Task<ParticipantSnapshotResult> ApplyParticipantSnapshotAsync(ParticipantSnapshotInput input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.MeetingId))
        {
            throw new ArgumentException("Meeting id is required.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.Source))
        {
            throw new ArgumentException("Snapshot source is required.", nameof(input));
        }

        var meetingId = input.MeetingId.Trim();
        var source = input.Source.Trim();
        var capturedAt = input.CapturedAt;

        var present = new Dictionary<string, (string DisplayName, string NormalizedName, string? Email)>(StringComparer.Ordinal);
        var emails = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ignored = new List<string>();
        if (input.ParticipantEmails is not null)
        {
            foreach (var (rawName, rawEmail) in input.ParticipantEmails)
            {
                var normalizedName = NameNormalizer.Normalize(rawName);
                if (!string.IsNullOrWhiteSpace(normalizedName))
                {
                    emails[normalizedName] = string.IsNullOrWhiteSpace(rawEmail) ? null : rawEmail.Trim();
                }
            }
        }

        if (input.Participants is { Count: > 0 })
        {
            foreach (var participant in input.Participants)
            {
                var displayName = participant.DisplayName?.Trim() ?? string.Empty;
                var normalized = NameNormalizer.Normalize(displayName);
                var presenceKey = participant.PresenceKey?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(presenceKey))
                {
                    continue;
                }

                present.TryAdd(
                    presenceKey,
                    (displayName, normalized, string.IsNullOrWhiteSpace(participant.Email) ? null : participant.Email.Trim()));
            }
        }
        else
        {
            foreach (var rawName in input.ParticipantNames ?? Array.Empty<string>())
            {
                var displayName = rawName?.Trim() ?? string.Empty;
                var normalized = NameNormalizer.Normalize(displayName);
                if (string.IsNullOrEmpty(normalized))
                {
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        ignored.Add(displayName);
                    }

                    continue;
                }

                emails.TryGetValue(normalized, out var participantEmail);

                // Manual snapshots have no stable Zoom identity, so normalized name is the key.
                present.TryAdd(normalized, (displayName, normalized, participantEmail));
            }
        }

        var snapshotLock = _snapshotLocks.GetOrAdd($"{meetingId}\u0000{source}", _ => new SemaphoreSlim(1, 1));
        await snapshotLock.WaitAsync(cancellationToken);
        try
        {
            var previous = await _repository.GetParticipantPresenceAsync(meetingId, source, cancellationToken);
            var previousByKey = previous.ToDictionary(entry => entry.PresenceKey ?? entry.NormalizedName, StringComparer.Ordinal);

            var roster = await _repository.GetRosterPeopleAsync(cancellationToken);
            var aliases = await _repository.GetAliasMapAsync(cancellationToken);

            var derivedEvents = new List<ParticipantEvent>();
            var joinedNames = new List<string>();
            var leftNames = new List<string>();
            var presentEntries = new List<ParticipantSnapshotEntry>(present.Count);

            foreach (var (presenceKey, participant) in present)
            {
                var (displayName, normalized, participantEmail) = participant;
                if (previousByKey.TryGetValue(presenceKey, out var existing))
                {
                    presentEntries.Add(existing with
                    {
                        DisplayName = displayName,
                        LastSeenAt = capturedAt,
                        ParticipantEmail = participantEmail ?? existing.ParticipantEmail
                    });
                    continue;
                }

                presentEntries.Add(new ParticipantSnapshotEntry(normalized, displayName, capturedAt, capturedAt, participantEmail, presenceKey));
                joinedNames.Add(displayName);
                derivedEvents.Add(CreateSnapshotEvent(
                    meetingId,
                    capturedAt,
                    ParticipantEventType.Joined,
                    displayName,
                    normalized,
                    participantEmail,
                    source,
                    roster,
                    aliases,
                    present.Count));
            }

            foreach (var entry in previous)
            {
                if (present.ContainsKey(entry.PresenceKey ?? entry.NormalizedName))
                {
                    continue;
                }

                leftNames.Add(entry.DisplayName);
                derivedEvents.Add(CreateSnapshotEvent(
                    meetingId,
                    capturedAt,
                    ParticipantEventType.Left,
                    entry.DisplayName,
                    entry.NormalizedName,
                    entry.ParticipantEmail,
                    source,
                    roster,
                    aliases,
                    present.Count));
            }

            await _repository.ApplyParticipantSnapshotAsync(meetingId, source, capturedAt, presentEntries, derivedEvents, cancellationToken);

            var board = await BuildBoardAsync(meetingId, cancellationToken);
            return new ParticipantSnapshotResult(
                MeetingId: meetingId,
                Source: source,
                CapturedAt: capturedAt,
                PresentCount: presentEntries.Count,
                JoinedNames: joinedNames,
                LeftNames: leftNames,
                IgnoredNames: ignored,
                Board: board);
        }
        finally
        {
            snapshotLock.Release();
        }
    }

    private ParticipantEvent CreateSnapshotEvent(
        string meetingId,
        DateTimeOffset occurredAt,
        ParticipantEventType eventType,
        string displayName,
        string normalizedName,
        string? participantEmail,
        string source,
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliases,
        int snapshotSize)
    {
        var candidate = _matcher.Match(roster, aliases, displayName, participantEmail);
        return new ParticipantEvent(
            Id: Guid.NewGuid().ToString("N"),
            MeetingId: meetingId,
            OccurredAt: occurredAt,
            EventType: eventType,
            ParticipantName: displayName,
            NormalizedParticipantName: normalizedName,
            ParticipantEmail: participantEmail,
            Confidence: candidate.Confidence,
            MatchedRosterPersonId: candidate.Person?.Id,
            Source: source,
            RawPayload: JsonSerializer.Serialize(new
            {
                snapshot = true,
                source,
                capturedAt = occurredAt,
                snapshotSize,
                name = displayName
            }));
    }

    public async Task<AttendanceBoard> BuildBoardAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var roster = await _repository.GetRosterPeopleAsync(cancellationToken);
        var events = await _repository.GetParticipantEventsAsync(meetingId, cancellationToken);
        var aliases = await _repository.GetAliasMapAsync(cancellationToken);
        var snapshots = await _repository.GetParticipantSnapshotSourcesAsync(meetingId, cancellationToken);
        var zoomSnapshot = snapshots.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Source, "zoom-live-participants", StringComparison.Ordinal));
        var authoritativeSnapshots = zoomSnapshot is null ? snapshots : new[] { zoomSnapshot };
        var currentPresence = new List<ParticipantSnapshotEntry>();
        foreach (var snapshot in authoritativeSnapshots)
        {
            currentPresence.AddRange(await _repository.GetParticipantPresenceAsync(meetingId, snapshot.Source, cancellationToken));
        }

        var currentCandidates = currentPresence
            .Select(entry => new
            {
                Entry = entry,
                Candidate = _matcher.Match(roster, aliases, entry.DisplayName, entry.ParticipantEmail)
            })
            .ToArray();
        var ambiguousPersonIds = currentCandidates
            .Where(item => item.Candidate.Person is not null)
            .GroupBy(item => item.Candidate.Person!.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var currentlyPresentPersonIds = currentCandidates
            .Where(item => item.Candidate.Person is not null && !ambiguousPersonIds.Contains(item.Candidate.Person.Id))
            .Select(item => item.Candidate.Person!.Id)
            .ToHashSet(StringComparer.Ordinal);

        var groupedByRosterPerson = events
            .Where(evt => !string.IsNullOrWhiteSpace(evt.MatchedRosterPersonId))
            .GroupBy(evt => evt.MatchedRosterPersonId!)
            .ToDictionary(group => group.Key, group => group.OrderBy(evt => evt.OccurredAt).ToList(), StringComparer.Ordinal);

        var people = roster.Select(person =>
        {
            groupedByRosterPerson.TryGetValue(person.Id, out var personEvents);
            personEvents ??= new List<ParticipantEvent>();

            var joined = personEvents.Where(evt => evt.EventType == ParticipantEventType.Joined).ToList();
            var lastEvent = personEvents.LastOrDefault();
            var lastJoin = joined.LastOrDefault()?.OccurredAt;
            var lastLeft = personEvents.LastOrDefault(evt => evt.EventType == ParticipantEventType.Left)?.OccurredAt;
            var confidence = personEvents.Any() ? personEvents.MaxBy(evt => evt.Confidence)?.Confidence ?? MatchConfidence.Unmatched : MatchConfidence.Unmatched;
            var reason = personEvents.Any() ? string.Join(", ", personEvents.Select(evt => evt.Confidence).Distinct()) : "No event yet";
            var attendanceState = snapshots.Count == 0
                ? ResolveState(lastEvent)
                : currentlyPresentPersonIds.Contains(person.Id)
                    ? AttendanceState.Present
                    : personEvents.Any(evt => evt.EventType == ParticipantEventType.Joined)
                        ? AttendanceState.Left
                        : AttendanceState.NotJoined;

            return new BoardPersonStatus(
                RosterPersonId: person.Id,
                Sequence: person.Sequence,
                Name: person.Name,
                Organization: person.Organization,
                AttendanceState: attendanceState,
                Confidence: confidence,
                ConfidenceReason: reason,
                LastJoinedAt: lastJoin,
                LastLeftAt: lastLeft,
                JoinCount: joined.Count);
        }).OrderBy(item => ParseSequence(item.Sequence)).ToArray();

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
                .Where(item => item.Candidate.Person is null || ambiguousPersonIds.Contains(item.Candidate.Person.Id))
                .Select(item =>
                {
                    var eventCount = events.Count(evt => evt.NormalizedParticipantName == item.Entry.NormalizedName);
                    return new UnmatchedParticipantStatus(
                        ParticipantName: item.Entry.DisplayName,
                        AttendanceState: AttendanceState.Present,
                        LastSeenAt: item.Entry.LastSeenAt,
                        EventCount: eventCount);
                })
                .OrderByDescending(item => item.LastSeenAt)
                .ToArray();

        var confidenceCounts = people
            .Where(person => person.AttendanceState != AttendanceState.NotJoined)
            .GroupBy(person => person.Confidence)
            .ToDictionary(group => group.Key, group => group.Count());

        return new AttendanceBoard(
            MeetingId: meetingId,
            GeneratedAt: DateTimeOffset.UtcNow,
            People: people,
            UnmatchedParticipants: unmatched,
            RecentEvents: events.OrderByDescending(evt => evt.OccurredAt).Take(30).ToArray(),
            ConfidenceCounts: confidenceCounts);
    }

    public async Task<string> BuildBoardCsvAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var board = await BuildBoardAsync(meetingId, cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine("Sequence,Name,Organization,AttendanceState,Confidence,ConfidenceReason,LastJoinedAt,LastLeftAt,JoinCount");

        foreach (var person in board.People)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                Escape(person.Sequence),
                Escape(person.Name),
                Escape(person.Organization),
                Escape(person.AttendanceState.ToString()),
                Escape(person.Confidence.ToString()),
                Escape(person.ConfidenceReason),
                Escape(person.LastJoinedAt?.ToString("O") ?? string.Empty),
                Escape(person.LastLeftAt?.ToString("O") ?? string.Empty),
                Escape(person.JoinCount.ToString())
            }));
        }

        return builder.ToString();
    }

    public async Task SaveAliasAsync(string alias, string rosterPersonId, string note, CancellationToken cancellationToken = default)
    {
        var normalizedAlias = NameNormalizer.Normalize(alias);
        if (string.IsNullOrWhiteSpace(normalizedAlias))
        {
            throw new InvalidOperationException("Alias cannot be empty after normalization.");
        }

        await _repository.UpsertAliasAsync(normalizedAlias, rosterPersonId, note, cancellationToken);
    }

    public async Task SeedDemoEventsAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var roster = await _repository.GetRosterPeopleAsync(cancellationToken);
        if (roster.Count == 0)
        {
            return;
        }

        var demoInputs = roster.Take(3).Select((person, index) => new ZoomParticipantEventInput(
            meetingId,
            DateTimeOffset.UtcNow.AddMinutes(-(15 - index * 3)),
            ParticipantEventType.Joined,
            index == 1 ? $"{person.Name} {person.Organization}" : person.Name,
            index == 0 ? person.Email : null,
            "demo-seed",
            JsonSerializer.Serialize(new { demo = true, name = person.Name }))).ToList();

        demoInputs.Add(new ZoomParticipantEventInput(
            meetingId,
            DateTimeOffset.UtcNow.AddMinutes(-4),
            ParticipantEventType.Joined,
            "김민수 iPhone",
            null,
            "demo-seed",
            "{\"demo\":true,\"name\":\"김민수 iPhone\"}"));

        foreach (var input in demoInputs)
        {
            await RecordParticipantEventAsync(input, cancellationToken);
        }
    }

    private static AttendanceState ResolveState(ParticipantEvent? participantEvent)
    {
        if (participantEvent is null)
        {
            return AttendanceState.NotJoined;
        }

        return participantEvent.EventType == ParticipantEventType.Joined ? AttendanceState.Present : AttendanceState.Left;
    }

    private static int ParseSequence(string value) => int.TryParse(value, out var parsed) ? parsed : int.MaxValue;

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
