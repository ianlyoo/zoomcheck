using System.Text.Json;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Services;

public sealed partial class AttendanceApplicationService
{
    /// <summary>
    /// Applies a full "currently present" participant list for a meeting and capture source.
    /// Names not seen before produce Joined events, names missing from the snapshot but present
    /// in that source's previous snapshot produce Left events. Presence is scoped by
    /// (meeting, source) so a snapshot never marks participants observed elsewhere as left.
    /// </summary>
    public async Task<ParticipantSnapshotResult> ApplyParticipantSnapshotAsync(ParticipantSnapshotInput input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.Source))
        {
            throw new ArgumentException("Snapshot source is required.", nameof(input));
        }

        var meetingId = MeetingIdNormalizer.Normalize(input.MeetingId);
        var source = input.Source.Trim();
        var capturedAt = input.CapturedAt;

        var rosterForCanonicalization = await _repository.GetRosterPeopleAsync(cancellationToken);
        var aliasesForCanonicalization = await _repository.GetAliasMapAsync(cancellationToken);
        var (present, ignored) = PrepareSnapshot(input, rosterForCanonicalization, aliasesForCanonicalization);

        return await WithMeetingLockAsync(meetingId, async () =>
        {
            var previous = await _repository.GetParticipantPresenceAsync(meetingId, source, cancellationToken);
            ReconcileChangedConnectorKeys(present, previous);
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
        }, cancellationToken);
    }

    private (Dictionary<string, PresentParticipant> Participants, List<string> IgnoredNames) PrepareSnapshot(
        ParticipantSnapshotInput input,
        IReadOnlyList<RosterPerson> roster,
        IReadOnlyDictionary<string, string> aliases)
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
                var canonicalName = ResolveCanonicalName(
                    rawName,
                    participantEmail,
                    roster,
                    aliases);
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
                    roster,
                    aliases);
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
        var missingPrevious = previous
            .Where(entry => !present.ContainsKey(entry.PresenceKey ?? entry.NormalizedName))
            .GroupBy(IdentityKey)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var previousKeys = previous.Select(entry => entry.PresenceKey ?? entry.NormalizedName)
            .ToHashSet(StringComparer.Ordinal);
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

    private sealed record PresentParticipant(
        string RawName,
        string DisplayName,
        string? CanonicalName,
        string NormalizedName,
        string? Email);

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
}
