using System.Text.Json;
using System.Text;
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

    public async Task<AttendanceBoard> BuildBoardAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        var roster = await _repository.GetRosterPeopleAsync(cancellationToken);
        var events = await _repository.GetParticipantEventsAsync(meetingId, cancellationToken);

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
            var confidence = personEvents.Any() ? personEvents.MinBy(evt => evt.Confidence)?.Confidence ?? MatchConfidence.Unmatched : MatchConfidence.Unmatched;
            var reason = personEvents.Any() ? string.Join(", ", personEvents.Select(evt => evt.Confidence).Distinct()) : "No event yet";

            return new BoardPersonStatus(
                RosterPersonId: person.Id,
                Sequence: person.Sequence,
                Name: person.Name,
                Organization: person.Organization,
                AttendanceState: ResolveState(lastEvent),
                Confidence: confidence,
                ConfidenceReason: reason,
                LastJoinedAt: lastJoin,
                LastLeftAt: lastLeft,
                JoinCount: joined.Count);
        }).OrderBy(item => ParseSequence(item.Sequence)).ToArray();

        var unmatched = events
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
            .ToArray();

        var confidenceCounts = people
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
