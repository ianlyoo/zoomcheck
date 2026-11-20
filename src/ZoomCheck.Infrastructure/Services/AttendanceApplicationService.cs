using System.Text.Json;
using System.Collections.Concurrent;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;

namespace ZoomCheck.Infrastructure.Services;

public sealed partial class AttendanceApplicationService
{
    private readonly ExcelRosterParser _rosterParser;
    private readonly SqliteAttendanceRepository _repository;
    private readonly AttendanceMatcher _matcher;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _meetingLocks = new(StringComparer.Ordinal);

    public AttendanceApplicationService(
        ExcelRosterParser rosterParser,
        SqliteAttendanceRepository repository,
        AttendanceMatcher matcher,
        TimeProvider? timeProvider = null)
    {
        _rosterParser = rosterParser;
        _repository = repository;
        _matcher = matcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private async Task<T> WithMeetingLockAsync<T>(string meetingId, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        var gate = _meetingLocks.GetOrAdd(meetingId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { gate.Release(); }
    }

    public async Task<RosterImportResult> ImportRosterAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var roster = _rosterParser.Parse(filePath);
        return await PersistRosterAsync(roster, cancellationToken);
    }

    public async Task<RosterImportResult> ImportRosterAsync(
        Stream stream,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var roster = _rosterParser.Parse(stream, $"upload://{displayName}", displayName);
        return await PersistRosterAsync(roster, cancellationToken);
    }

    private async Task<RosterImportResult> PersistRosterAsync(RosterImportResult roster, CancellationToken cancellationToken)
    {
        var reconciliation = await _repository.ReplaceRosterWithReconciliationAsync(roster, cancellationToken);
        return roster with
        {
            People = roster.People.Select(person => person with { Id = reconciliation.ResolveId(person) }).ToArray()
        };
    }

    public Task<IReadOnlyList<RosterPerson>> GetRosterAsync(CancellationToken cancellationToken = default)
        => _repository.GetRosterPeopleAsync(cancellationToken);

    public Task<IReadOnlyList<ParticipantEvent>> GetParticipantEventsForMeetingAsync(string meetingId, CancellationToken cancellationToken = default)
        => _repository.GetParticipantEventsAsync(MeetingIdNormalizer.Normalize(meetingId), cancellationToken);

    public Task<ParticipantEvent> RecordParticipantEventAsync(ParticipantEventInput input, CancellationToken cancellationToken = default)
        => WithMeetingLockAsync(MeetingIdNormalizer.Normalize(input.MeetingId),
            () => RecordParticipantEventCoreAsync(input, cancellationToken), cancellationToken);

    private async Task<ParticipantEvent> RecordParticipantEventCoreAsync(ParticipantEventInput input, CancellationToken cancellationToken)
    {
        var meetingId = MeetingIdNormalizer.Normalize(input.MeetingId);
        var roster = await _repository.GetRosterPeopleAsync(cancellationToken);
        var aliases = await _repository.GetAliasMapAsync(cancellationToken);
        var rawName = input.ParticipantName;
        var canonicalName = ResolveCanonicalName(rawName, input.ParticipantEmail, roster, aliases);
        var effectiveName = canonicalName ?? rawName;
        var candidate = MatchParticipant(roster, aliases, rawName, effectiveName, input.ParticipantEmail);

        var participantEvent = new ParticipantEvent(
            Id: Guid.NewGuid().ToString("N"),
            MeetingId: meetingId,
            OccurredAt: input.OccurredAt,
            EventType: input.EventType,
            ParticipantName: effectiveName,
            NormalizedParticipantName: NameNormalizer.Normalize(effectiveName),
            ParticipantEmail: input.ParticipantEmail,
            Confidence: candidate.Confidence,
            MatchedRosterPersonId: candidate.Person?.Id,
            Source: input.Source,
            RawPayload: input.RawPayload,
            RawParticipantName: rawName,
            CanonicalParticipantName: canonicalName);

        await _repository.AppendParticipantEventAsync(participantEvent, cancellationToken);
        return participantEvent;
    }

    public Task<ParticipantEvent> RecordZoomEventAsync(ZoomParticipantEventInput input, CancellationToken cancellationToken = default)
        => RecordParticipantEventAsync(input, cancellationToken);

    private string? ResolveCanonicalName(
        string rawName,
        string? participantEmail,
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliases)
    {
        if (!KoreanNameCanonicalizer.TryCanonicalize(rawName, roster, out var canonicalName, out var canonicalPerson))
        {
            return null;
        }

        // Explicit identity evidence must win over an automatic display-name rewrite. If an
        // email or operator-saved alias says this is a different roster person, leave the raw
        // name untouched instead of presenting a misleading canonical name.
        var rawCandidate = _matcher.Match(roster, aliases, rawName, participantEmail);
        var hasExplicitRawMatch = rawCandidate.Confidence is MatchConfidence.Verified or MatchConfidence.AliasVerified;
        if (hasExplicitRawMatch
            && rawCandidate.Person is not null
            && !string.Equals(rawCandidate.Person.Id, canonicalPerson!.Id, StringComparison.Ordinal))
        {
            return null;
        }

        return canonicalName;
    }

    private MatchCandidate MatchParticipant(
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliases,
        string rawName,
        string displayName,
        string? participantEmail)
    {
        var rawCandidate = _matcher.Match(roster, aliases, rawName, participantEmail);
        if (rawCandidate.Confidence is MatchConfidence.Verified or MatchConfidence.AliasVerified)
        {
            return rawCandidate;
        }

        return string.Equals(rawName, displayName, StringComparison.Ordinal)
            ? rawCandidate
            : _matcher.Match(roster, aliases, displayName, participantEmail);
    }

    /// <summary>
    /// Exports the board as CSV. When <paramref name="groupFilter"/> is supplied, only people in
    /// that roster group are exported; the comparison ignores case and surrounding or repeated
    /// whitespace so "1조" and " 1 조 " select the same group.
    /// </summary>
    public async Task<string> BuildBoardCsvAsync(
        string meetingId,
        string? groupFilter = null,
        CancellationToken cancellationToken = default)
    {
        var board = await BuildBoardAsync(meetingId, cancellationToken);
        return AttendanceBoardCsvWriter.Write(board, groupFilter);
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
        meetingId = MeetingIdNormalizer.Normalize(meetingId);
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
}
