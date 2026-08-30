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
        var rawName = input.ParticipantName;
        var canonicalName = ResolveCanonicalName(rawName, input.ParticipantEmail, roster, aliases);
        var effectiveName = canonicalName ?? rawName;
        var candidate = MatchParticipant(roster, aliases, rawName, effectiveName, input.ParticipantEmail);

        var participantEvent = new ParticipantEvent(
            Id: Guid.NewGuid().ToString("N"),
            MeetingId: input.MeetingId,
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

        var rosterForCanonicalization = await _repository.GetRosterPeopleAsync(cancellationToken);
        var aliasesForCanonicalization = await _repository.GetAliasMapAsync(cancellationToken);
        var present = new Dictionary<string, PresentParticipant>(StringComparer.Ordinal);
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
                var rawName = participant.DisplayName?.Trim() ?? string.Empty;
                var presenceKey = participant.PresenceKey?.Trim() ?? string.Empty;
                var participantEmail = string.IsNullOrWhiteSpace(participant.Email) ? null : participant.Email.Trim();
                var canonicalName = ResolveCanonicalName(
                    rawName,
                    participantEmail,
                    rosterForCanonicalization,
                    aliasesForCanonicalization);
                var displayName = canonicalName ?? rawName;
                var normalized = NameNormalizer.Normalize(displayName);
                if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(presenceKey))
                {
                    continue;
                }

                present.TryAdd(
                    presenceKey,
                    new PresentParticipant(
                        rawName,
                        displayName,
                        canonicalName,
                        normalized,
                        participantEmail));
            }
        }
        else
        {
            foreach (var rawName in input.ParticipantNames ?? Array.Empty<string>())
            {
                var trimmedRawName = rawName?.Trim() ?? string.Empty;
                var rawNormalized = NameNormalizer.Normalize(trimmedRawName);
                emails.TryGetValue(rawNormalized, out var participantEmail);
                var canonicalName = ResolveCanonicalName(
                    trimmedRawName,
                    participantEmail,
                    rosterForCanonicalization,
                    aliasesForCanonicalization);
                var displayName = canonicalName ?? trimmedRawName;
                var normalized = NameNormalizer.Normalize(displayName);
                if (string.IsNullOrEmpty(normalized))
                {
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        ignored.Add(displayName);
                    }

                    continue;
                }

                if (participantEmail is null && !emails.TryGetValue(normalized, out participantEmail))
                {
                    emails.TryGetValue(rawNormalized, out participantEmail);
                }

                // Manual snapshots have no stable Zoom identity. Keep their key tied to the raw
                // submitted name so importing a roster later cannot manufacture a leave + join
                // merely because canonicalization became available.
                present.TryAdd(
                    rawNormalized,
                    new PresentParticipant(trimmedRawName, displayName, canonicalName, normalized, participantEmail));
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
            var nameChanges = new List<ParticipantNameChange>();
            var presentEntries = new List<ParticipantSnapshotEntry>(present.Count);

            foreach (var (presenceKey, participant) in present)
            {
                var rawName = participant.RawName;
                var displayName = participant.DisplayName;
                var canonicalName = participant.CanonicalName;
                var normalized = participant.NormalizedName;
                var participantEmail = participant.Email;
                if (previousByKey.TryGetValue(presenceKey, out var existing))
                {
                    var resolvedEmail = participantEmail ?? existing.ParticipantEmail;
                    var updated = existing with
                    {
                        DisplayName = displayName,
                        NormalizedName = normalized,
                        RawDisplayName = rawName,
                        CanonicalName = canonicalName,
                        LastSeenAt = capturedAt,
                        ParticipantEmail = resolvedEmail
                    };
                    presentEntries.Add(updated);

                    // Same connection, different name: record it so the operator can see the rename.
                    // Compare on the normalized name so re-submitting the same name with different
                    // spacing or punctuation is not reported as a rename.
                    var previousRawName = existing.EffectiveRawDisplayName;
                    if (!string.Equals(existing.NormalizedName, normalized, StringComparison.Ordinal))
                    {
                        nameChanges.Add(new ParticipantNameChange(
                            PresenceKey: presenceKey,
                            PreviousRawName: previousRawName,
                            PreviousName: existing.DisplayName,
                            RawName: rawName,
                            Name: displayName,
                            CanonicalName: canonicalName,
                            OccurredAt: capturedAt));

                        derivedEvents.Add(CreateSnapshotEvent(
                            meetingId,
                            capturedAt,
                            ParticipantEventType.NameChanged,
                            rawName,
                            displayName,
                            canonicalName,
                            normalized,
                            resolvedEmail,
                            source,
                            roster,
                            aliases,
                            present.Count,
                            presenceKey,
                            previousName: existing.DisplayName,
                            previousRawName: previousRawName));
                    }

                    continue;
                }

                presentEntries.Add(new ParticipantSnapshotEntry(
                    normalized,
                    displayName,
                    capturedAt,
                    capturedAt,
                    participantEmail,
                    presenceKey,
                    rawName,
                    canonicalName));
                joinedNames.Add(displayName);
                derivedEvents.Add(CreateSnapshotEvent(
                    meetingId,
                    capturedAt,
                    ParticipantEventType.Joined,
                    rawName,
                    displayName,
                    canonicalName,
                    normalized,
                    participantEmail,
                    source,
                    roster,
                    aliases,
                    present.Count,
                    presenceKey));
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
                    entry.EffectiveRawDisplayName,
                    entry.DisplayName,
                    entry.CanonicalName,
                    entry.NormalizedName,
                    entry.ParticipantEmail,
                    source,
                    roster,
                    aliases,
                    present.Count,
                    entry.PresenceKey ?? entry.NormalizedName));
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
                Board: board,
                NameChanges: nameChanges);
        }
        finally
        {
            snapshotLock.Release();
        }
    }

    private sealed record PresentParticipant(
        string RawName,
        string DisplayName,
        string? CanonicalName,
        string NormalizedName,
        string? Email);

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

    private ParticipantEvent CreateSnapshotEvent(
        string meetingId,
        DateTimeOffset occurredAt,
        ParticipantEventType eventType,
        string rawName,
        string displayName,
        string? canonicalName,
        string normalizedName,
        string? participantEmail,
        string source,
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliases,
        int snapshotSize,
        string? presenceKey = null,
        string? previousName = null,
        string? previousRawName = null)
    {
        var candidate = MatchParticipant(roster, aliases, rawName, displayName, participantEmail);
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
                name = displayName,
                rawName,
                canonicalName,
                presenceKey,
                previousName,
                previousRawName
            }),
            PresenceKey: presenceKey,
            RawParticipantName: rawName,
            CanonicalParticipantName: canonicalName,
            PreviousParticipantName: previousName,
            PreviousRawParticipantName: previousRawName);
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
        var sourceByPresenceKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in authoritativeSnapshots)
        {
            var entries = await _repository.GetParticipantPresenceAsync(meetingId, snapshot.Source, cancellationToken);
            currentPresence.AddRange(entries);
            foreach (var entry in entries)
            {
                sourceByPresenceKey[entry.PresenceKey ?? entry.NormalizedName] = snapshot.Source;
            }
        }

        var currentCandidates = currentPresence
            .Select(entry => new
            {
                Entry = entry,
                Candidate = MatchParticipant(
                    roster,
                    aliases,
                    entry.EffectiveRawDisplayName,
                    entry.DisplayName,
                    entry.ParticipantEmail)
            })
            .ToArray();
        var currentConnections = currentCandidates
            .Select(item =>
            {
                var presenceKey = item.Entry.PresenceKey ?? item.Entry.NormalizedName;
                sourceByPresenceKey.TryGetValue(presenceKey, out var connectionSource);
                return new CurrentParticipantConnection(
                    PresenceKey: presenceKey,
                    Source: connectionSource ?? string.Empty,
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
                    MatchReason: item.Candidate.Reason);
            })
            .OrderBy(connection => connection.FirstSeenAt)
            .ToArray();

        // A roster person matched by several live connections is still present exactly once.
        // The extra connections surface as review metadata instead of cancelling attendance.
        var connectionsByPerson = currentConnections
            .Where(connection => connection.MatchedRosterPersonId is not null)
            .GroupBy(connection => connection.MatchedRosterPersonId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        var duplicateConnectionGroups = connectionsByPerson
            .Where(pair => pair.Value.Length > 1)
            .Select(pair => new DuplicateConnectionGroup(
                RosterPersonId: pair.Key,
                RosterPersonName: pair.Value[0].MatchedRosterPersonName ?? string.Empty,
                ConnectionCount: pair.Value.Length,
                Connections: pair.Value))
            .OrderByDescending(group => group.ConnectionCount)
            .ThenBy(group => group.RosterPersonId, StringComparer.Ordinal)
            .ToArray();

        var currentlyPresentPersonIds = connectionsByPerson.Keys.ToHashSet(StringComparer.Ordinal);

        var groupedByRosterPerson = events
            .Where(evt => !string.IsNullOrWhiteSpace(evt.MatchedRosterPersonId))
            .GroupBy(evt => evt.MatchedRosterPersonId!)
            .ToDictionary(group => group.Key, group => group.OrderBy(evt => evt.OccurredAt).ToList(), StringComparer.Ordinal);

        var people = roster.Select(person =>
        {
            groupedByRosterPerson.TryGetValue(person.Id, out var personEvents);
            personEvents ??= new List<ParticipantEvent>();

            var joined = personEvents.Where(evt => evt.EventType == ParticipantEventType.Joined).ToList();
            var lastEvent = personEvents.LastOrDefault(evt => evt.EventType != ParticipantEventType.NameChanged);
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
                JoinCount: joined.Count,
                ActiveConnectionCount: connectionsByPerson.TryGetValue(person.Id, out var personConnections) ? personConnections.Length : 0,
                HasDuplicateConnections: connectionsByPerson.TryGetValue(person.Id, out var duplicateCheck) && duplicateCheck.Length > 1);
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
                .Where(item => item.Candidate.Person is null)
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
            ConfidenceCounts: confidenceCounts,
            CurrentConnections: currentConnections,
            DuplicateConnectionGroups: duplicateConnectionGroups);
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

        return participantEvent.EventType switch
        {
            ParticipantEventType.Joined => AttendanceState.Present,
            // A rename does not change whether the participant is in the meeting.
            ParticipantEventType.NameChanged => AttendanceState.Present,
            _ => AttendanceState.Left
        };
    }

    private static int ParseSequence(string value) => int.TryParse(value, out var parsed) ? parsed : int.MaxValue;

    private static string Escape(string value)
    {
        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }
}
