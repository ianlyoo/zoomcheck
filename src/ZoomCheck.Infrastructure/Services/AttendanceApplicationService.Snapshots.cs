using System.Text.Json;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;

namespace ZoomCheck.Infrastructure.Services;

public sealed partial class AttendanceApplicationService
{
    /// <summary>
    /// Ingests and persists a full "currently present" participant snapshot without projecting
    /// an attendance board. Suitable for background sync paths whose callers read the board separately.
    /// </summary>
    public async Task<ParticipantSnapshotIngestionResult> IngestParticipantSnapshotAsync(
        ParticipantSnapshotInput input,
        CancellationToken cancellationToken = default)
    {
        var (meetingId, source, capturedAt) = ValidateSnapshotInput(input);
        return await WithMeetingLockAsync(meetingId, async () =>
        {
            var processed = await ProcessSnapshotCoreAsync(meetingId, source, capturedAt, input, cancellationToken);
            await _repository.PersistSnapshotDeltaAsync(
                meetingId,
                source,
                capturedAt,
                processed.PresentEntries.Count,
                processed.Delta,
                processed.DerivedEvents,
                cancellationToken);

            return new ParticipantSnapshotIngestionResult(
                MeetingId: meetingId,
                Source: source,
                CapturedAt: capturedAt,
                PresentCount: processed.PresentEntries.Count,
                JoinedNames: processed.JoinedNames,
                LeftNames: processed.LeftNames,
                IgnoredNames: processed.IgnoredNames,
                NameChanges: processed.NameChanges);
        }, cancellationToken);
    }

    /// <summary>
    /// Applies a full "currently present" participant list for a meeting and capture source.
    /// Names not seen before produce Joined events, names missing from the snapshot but present
    /// in that source's previous snapshot produce Left events. Presence is scoped by
    /// (meeting, source) so a snapshot never marks participants observed elsewhere as left.
    /// </summary>
    public async Task<ParticipantSnapshotResult> ApplyParticipantSnapshotAsync(
        ParticipantSnapshotInput input,
        CancellationToken cancellationToken = default)
    {
        var (meetingId, source, capturedAt) = ValidateSnapshotInput(input);
        return await WithMeetingLockAsync(meetingId, async () =>
        {
            var processed = await ProcessSnapshotCoreAsync(meetingId, source, capturedAt, input, cancellationToken);
            var attendanceDate = CurrentAttendanceDate();
            var projectionData = await _repository.PersistSnapshotDeltaAndReadBoardAsync(
                meetingId,
                source,
                capturedAt,
                processed.PresentEntries,
                processed.Delta,
                processed.DerivedEvents,
                processed.MatchIndex.Roster,
                processed.MatchIndex.AliasMap,
                attendanceDate,
                cancellationToken);

            var board = AttendanceBoardProjector.Project(
                meetingId,
                attendanceDate,
                projectionData,
                processed.MatchIndex);

            return new ParticipantSnapshotResult(
                MeetingId: meetingId,
                Source: source,
                CapturedAt: capturedAt,
                PresentCount: processed.PresentEntries.Count,
                JoinedNames: processed.JoinedNames,
                LeftNames: processed.LeftNames,
                IgnoredNames: processed.IgnoredNames,
                Board: board,
                NameChanges: processed.NameChanges);
        }, cancellationToken);
    }

    private static (string MeetingId, string Source, DateTimeOffset CapturedAt) ValidateSnapshotInput(
        ParticipantSnapshotInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Source))
        {
            throw new ArgumentException("Snapshot source is required.", nameof(input));
        }

        return (MeetingIdNormalizer.Normalize(input.MeetingId), input.Source.Trim(), input.CapturedAt);
    }

    private async Task<ProcessedSnapshot> ProcessSnapshotCoreAsync(
        string meetingId,
        string source,
        DateTimeOffset capturedAt,
        ParticipantSnapshotInput input,
        CancellationToken cancellationToken)
    {
        var context = await _repository.ReadSnapshotContextAsync(meetingId, source, cancellationToken);
        var matchIndex = new AttendanceMatchIndex(context.Roster, context.Aliases);
        var (present, ignored) = PrepareSnapshot(input, matchIndex);

        var previous = context.PreviousPresence;
        ReconcileChangedConnectorKeys(present, previous);
        var previousByKey = previous.ToDictionary(
            entry => entry.PresenceKey ?? entry.NormalizedName,
            StringComparer.Ordinal);

        var derivedEvents = new List<ParticipantEvent>();
        var joinedNames = new List<string>();
        var leftNames = new List<string>();
        var nameChanges = new List<ParticipantNameChange>();
        var presentEntries = new List<ParticipantSnapshotEntry>(present.Count);
        var addedEntries = new List<ParticipantSnapshotEntry>();
        var updatedEntries = new List<ParticipantSnapshotEntry>();
        var removedKeys = new List<string>();

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

                if (HasPresenceMetadataChanged(existing, updated))
                {
                    updatedEntries.Add(updated);
                }

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
                        matchIndex,
                        present.Count,
                        presenceKey,
                        previousName: existing.DisplayName,
                        previousRawName: previousRawName));
                }

                continue;
            }

            var added = new ParticipantSnapshotEntry(
                normalized,
                displayName,
                capturedAt,
                capturedAt,
                participantEmail,
                presenceKey,
                rawName,
                canonicalName);
            presentEntries.Add(added);
            addedEntries.Add(added);
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
                matchIndex,
                present.Count,
                presenceKey));
        }

        foreach (var entry in previous)
        {
            var key = entry.PresenceKey ?? entry.NormalizedName;
            if (present.ContainsKey(key))
            {
                continue;
            }

            removedKeys.Add(key);
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
                matchIndex,
                present.Count,
                key));
        }

        return new ProcessedSnapshot(
            MatchIndex: matchIndex,
            PresentEntries: presentEntries,
            Delta: new SnapshotDelta(removedKeys, addedEntries, updatedEntries),
            DerivedEvents: derivedEvents,
            JoinedNames: joinedNames,
            LeftNames: leftNames,
            IgnoredNames: ignored,
            NameChanges: nameChanges);
    }

    private static bool HasPresenceMetadataChanged(
        ParticipantSnapshotEntry existing,
        ParticipantSnapshotEntry updated)
        => !string.Equals(existing.DisplayName, updated.DisplayName, StringComparison.Ordinal)
            || !string.Equals(existing.NormalizedName, updated.NormalizedName, StringComparison.Ordinal)
            || !string.Equals(existing.RawDisplayName, updated.RawDisplayName, StringComparison.Ordinal)
            || !string.Equals(existing.CanonicalName, updated.CanonicalName, StringComparison.Ordinal)
            || !string.Equals(existing.ParticipantEmail, updated.ParticipantEmail, StringComparison.Ordinal);

    private static (Dictionary<string, PresentParticipant> Participants, List<string> IgnoredNames) PrepareSnapshot(
        ParticipantSnapshotInput input,
        AttendanceMatchIndex matchIndex)
    {
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
                var canonicalName = matchIndex.ResolveCanonicalName(rawName, participantEmail);
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
                var canonicalName = matchIndex.ResolveCanonicalName(trimmedRawName, participantEmail);
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

        return (present, ignored);
    }

    /// <summary>
    /// Dashboard API ids and Zoom Apps participantUUIDs are not guaranteed to use the
    /// same value. During a connector switch, preserve an existing connection key only
    /// when one missing old connection and one new connection share a unique identity.
    /// Ambiguous duplicate names are deliberately left untouched for operator review.
    /// </summary>
    private static void ReconcileChangedConnectorKeys(
        Dictionary<string, PresentParticipant> present,
        IReadOnlyList<ParticipantSnapshotEntry> previous)
    {
        if (previous.Count == 0 || present.Count == 0)
        {
            return;
        }

        var previousKeys = new HashSet<string>(previous.Count, StringComparer.Ordinal);
        var missingList = new List<ParticipantSnapshotEntry>();
        foreach (var entry in previous)
        {
            var key = entry.PresenceKey ?? entry.NormalizedName;
            previousKeys.Add(key);
            if (!present.ContainsKey(key))
            {
                missingList.Add(entry);
            }
        }

        if (missingList.Count == 0)
        {
            return;
        }

        var missingPrevious = missingList
            .GroupBy(IdentityKey)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        if (missingPrevious.Count == 0)
        {
            return;
        }

        var newConnections = present
            .Where(item => !previousKeys.Contains(item.Key))
            .GroupBy(item => IdentityKey(item.Value))
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        foreach (var identity in missingPrevious.Keys.Intersect(newConnections.Keys, StringComparer.Ordinal))
        {
            var oldKey = missingPrevious[identity].PresenceKey ?? missingPrevious[identity].NormalizedName;
            var incoming = newConnections[identity];
            if (!IsConnectorSwitch(oldKey, incoming.Key))
            {
                continue;
            }

            present.Remove(incoming.Key);
            present.TryAdd(oldKey, incoming.Value);
        }
    }

    private static string IdentityKey(ParticipantSnapshotEntry entry)
        => !string.IsNullOrWhiteSpace(entry.ParticipantEmail)
            ? $"email:{entry.ParticipantEmail.Trim().ToLowerInvariant()}"
            : $"name:{entry.NormalizedName}";

    private static string IdentityKey(PresentParticipant participant)
        => !string.IsNullOrWhiteSpace(participant.Email)
            ? $"email:{participant.Email.Trim().ToLowerInvariant()}"
            : $"name:{participant.NormalizedName}";

    private static bool IsConnectorSwitch(string previousKey, string incomingKey)
        => previousKey.StartsWith("zoom-app:", StringComparison.Ordinal)
            != incomingKey.StartsWith("zoom-app:", StringComparison.Ordinal)
            && (IsBusinessZoomKey(previousKey) || IsBusinessZoomKey(incomingKey));

    private static bool IsBusinessZoomKey(string key)
        => key.StartsWith("zoom-id:", StringComparison.Ordinal)
            || key.StartsWith("zoom-user:", StringComparison.Ordinal)
            || key.StartsWith("zoom-name:", StringComparison.Ordinal);

    private static ParticipantEvent CreateSnapshotEvent(
        string meetingId,
        DateTimeOffset occurredAt,
        ParticipantEventType eventType,
        string rawName,
        string displayName,
        string? canonicalName,
        string normalizedName,
        string? participantEmail,
        string source,
        AttendanceMatchIndex matchIndex,
        int snapshotSize,
        string? presenceKey = null,
        string? previousName = null,
        string? previousRawName = null)
    {
        var candidate = matchIndex.MatchParticipant(rawName, displayName, participantEmail);
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

    private sealed record PresentParticipant(
        string RawName,
        string DisplayName,
        string? CanonicalName,
        string NormalizedName,
        string? Email);

    private sealed record ProcessedSnapshot(
        AttendanceMatchIndex MatchIndex,
        List<ParticipantSnapshotEntry> PresentEntries,
        SnapshotDelta Delta,
        List<ParticipantEvent> DerivedEvents,
        List<string> JoinedNames,
        List<string> LeftNames,
        List<string> IgnoredNames,
        List<ParticipantNameChange> NameChanges);
}
