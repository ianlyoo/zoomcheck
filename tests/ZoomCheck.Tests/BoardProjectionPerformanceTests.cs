using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

public sealed class BoardProjectionPerformanceTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"zoomcheck-projection-{Guid.NewGuid():N}.db");

    private SqliteAttendanceRepository _repository = null!;
    private AttendanceApplicationService _service = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteAttendanceRepository(_databasePath);
        await _repository.InitializeAsync();
        _service = new AttendanceApplicationService(
            new ExcelRosterParser(),
            _repository,
            new AttendanceMatcher());
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task IngestParticipantSnapshot_PersistsSameBoardState_AsApplyParticipantSnapshot()
    {
        await _repository.ReplaceRosterAsync(new RosterImportResult(
            "r1", "test.xlsx", "test.xlsx", DateTimeOffset.UtcNow,
            new[]
            {
                Person("p1", "1", "학생001", "student001@example.com"),
                Person("p2", "2", "학생002", "student002@example.com"),
                Person("p3", "3", "학생003", "student003@example.com")
            }));

        var t1 = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(5);

        var first = await _service.IngestParticipantSnapshotAsync(new ParticipantSnapshotInput(
            "123456789",
            new[] { "학생001", "학생002", "손님A" },
            "zoom-live-participants",
            t1,
            Participants: new[]
            {
                new ParticipantSnapshotParticipant("zoom-id:1", "학생001", "student001@example.com"),
                new ParticipantSnapshotParticipant("zoom-id:2", "학생002", "student002@example.com"),
                new ParticipantSnapshotParticipant("zoom-id:guest", "손님A", null)
            }));

        Assert.Equal(3, first.PresentCount);
        Assert.Equal(3, first.JoinedNames.Count);
        Assert.Empty(first.LeftNames);

        var second = await _service.IngestParticipantSnapshotAsync(new ParticipantSnapshotInput(
            "123456789",
            new[] { "학생001", "학생003" },
            "zoom-live-participants",
            t2,
            Participants: new[]
            {
                new ParticipantSnapshotParticipant("zoom-id:1", "학생001", "student001@example.com"),
                new ParticipantSnapshotParticipant("zoom-id:3", "학생003", "student003@example.com")
            }));

        Assert.Equal(2, second.PresentCount);
        Assert.Equal(new[] { "학생003" }, second.JoinedNames);
        Assert.Equal(2, second.LeftNames.Count);

        var board = await _service.BuildBoardAsync("123456789");
        Assert.Equal(AttendanceState.Present, board.People.Single(p => p.RosterPersonId == "p1").AttendanceState);
        Assert.Equal(AttendanceState.Left, board.People.Single(p => p.RosterPersonId == "p2").AttendanceState);
        Assert.Equal(t2, board.People.Single(p => p.RosterPersonId == "p2").LastLeftAt);
        Assert.Equal(AttendanceState.Present, board.People.Single(p => p.RosterPersonId == "p3").AttendanceState);
        Assert.Empty(board.UnmatchedParticipants);
        Assert.Equal(t2, board.LastReceivedAt);
        Assert.Equal(t2, board.LatestEventAt);
    }

    [Fact]
    public async Task DeepEventHistory_PreservesExactJoinCountsRecentWindowAndManualMatchProjection()
    {
        var rosterPeople = Enumerable.Range(1, 20)
            .Select(i => Person($"p{i}", i.ToString(), $"학생{i:D3}", $"student{i:D3}@example.com"))
            .ToArray();
        await _repository.ReplaceRosterAsync(new RosterImportResult("r1", "test.xlsx", "test.xlsx", DateTimeOffset.UtcNow, rosterPeople));

        var start = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        for (var tick = 0; tick < 30; tick++)
        {
            var capturedAt = start.AddMinutes(tick);
            var active = rosterPeople
                .Where((_, idx) => (idx + tick) % 2 == 0)
                .Select(p => new ParticipantSnapshotParticipant($"zoom-id:{p.Id}", p.Name, p.Email))
                .Append(new ParticipantSnapshotParticipant("zoom-id:guest-1", "미등록기기", null))
                .ToArray();

            await _service.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
                "987654321",
                active.Select(a => a.DisplayName).ToArray(),
                "zoom-live-participants",
                capturedAt,
                Participants: active));
        }

        // Reopen on a brand new repository to verify persisted summaries survive restart.
        var reopenedRepo = new SqliteAttendanceRepository(_databasePath);
        await reopenedRepo.InitializeAsync();
        var reopenedService = new AttendanceApplicationService(new ExcelRosterParser(), reopenedRepo, new AttendanceMatcher());

        var board = await reopenedService.BuildBoardAsync("987654321");
        Assert.Equal(30, board.RecentEvents.Count);
        Assert.Equal(start.AddMinutes(29), board.LatestEventAt);

        // Even-indexed roster people join on ticks 0, 2, 4, ..., 28 => 15 joins.
        var p1 = board.People.Single(p => p.RosterPersonId == "p1");
        Assert.Equal(15, p1.JoinCount);
        Assert.Equal(AttendanceState.Left, p1.AttendanceState);
        Assert.Equal(start.AddMinutes(28), p1.LastJoinedAt);
        Assert.Equal(start.AddMinutes(29), p1.LastLeftAt);

        // Odd-indexed roster people join on ticks 1, 3, ..., 29 => 15 joins and remain present at tick 29.
        var p2 = board.People.Single(p => p.RosterPersonId == "p2");
        Assert.Equal(15, p2.JoinCount);
        Assert.Equal(AttendanceState.Present, p2.AttendanceState);
        Assert.Equal(start.AddMinutes(29), p2.LastJoinedAt);

        // Unmatched connection still accurately links via manual match without scanning full history.
        var guest = Assert.Single(board.CurrentConnections!, c => c.PresenceKey == "zoom-id:guest-1");
        var matchedBoard = await reopenedService.SetConnectionMatchAsync(
            "987654321", "zoom-live-participants", "zoom-id:guest-1", "p1", guest.EvidenceToken);
        var updatedP1 = matchedBoard.People.Single(p => p.RosterPersonId == "p1");
        Assert.Equal(AttendanceState.Present, updatedP1.AttendanceState);
        Assert.Equal(16, updatedP1.JoinCount);
    }

    [Fact]
    public async Task Initialize_BackfillsEventProjectionSummaries_ForLegacyDatabases()
    {
        var legacyPath = Path.Combine(Path.GetTempPath(), $"zoomcheck-legacy-proj-{Guid.NewGuid():N}.db");
        try
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = legacyPath, Pooling = false }.ToString();
            await using (var conn = new SqliteConnection(connectionString))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE roster_people (
                        id TEXT PRIMARY KEY, import_id TEXT NOT NULL, sequence TEXT NOT NULL,
                        name TEXT NOT NULL, normalized_name TEXT NOT NULL, email TEXT NOT NULL,
                        phone TEXT NOT NULL, organization TEXT NOT NULL, aliases_json TEXT NOT NULL,
                        group_name TEXT NOT NULL DEFAULT ''
                    );
                    CREATE TABLE participant_events (
                        id TEXT PRIMARY KEY, meeting_id TEXT NOT NULL, occurred_at TEXT NOT NULL,
                        event_type TEXT NOT NULL, participant_name TEXT NOT NULL,
                        normalized_participant_name TEXT NOT NULL, participant_email TEXT NULL,
                        confidence TEXT NOT NULL, matched_roster_person_id TEXT NULL,
                        source TEXT NOT NULL, raw_payload TEXT NOT NULL
                    );
                    INSERT INTO roster_people VALUES ('p1','imp1','1','학생001','학생001','student001@example.com','','','[]','');
                    INSERT INTO participant_events VALUES ('e1','555666777','2026-10-01T09:00:00.0000000+00:00','Joined','학생001','학생001','student001@example.com','Verified','p1','webhook','{}');
                    INSERT INTO participant_events VALUES ('e2','555666777','2026-10-01T09:10:00.0000000+00:00','Left','학생001','학생001','student001@example.com','Verified','p1','webhook','{}');
                    INSERT INTO participant_events VALUES ('e3','555666777','2026-10-01T09:15:00.0000000+00:00','Joined','학생001','학생001','student001@example.com','Verified','p1','webhook','{}');
                    INSERT INTO participant_events VALUES ('e4','555666777','2026-10-01T09:20:00.0000000+00:00','Joined','외부인','외부인',NULL,'Unmatched',NULL,'webhook','{}');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            var repo = new SqliteAttendanceRepository(legacyPath);
            await repo.InitializeAsync();
            var service = new AttendanceApplicationService(new ExcelRosterParser(), repo, new AttendanceMatcher());

            var board = await service.BuildBoardAsync("555666777");
            var person = Assert.Single(board.People);
            Assert.Equal(AttendanceState.Present, person.AttendanceState);
            Assert.Equal(2, person.JoinCount);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 15, 0, TimeSpan.Zero), person.LastJoinedAt);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 10, 0, TimeSpan.Zero), person.LastLeftAt);

            var unmatched = Assert.Single(board.UnmatchedParticipants);
            Assert.Equal("외부인", unmatched.ParticipantName);
            Assert.Equal(AttendanceState.Present, unmatched.AttendanceState);
            Assert.Equal(1, unmatched.EventCount);
            Assert.Equal(4, board.RecentEvents.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-shm", "-wal" })
            {
                if (File.Exists(legacyPath + suffix))
                {
                    File.Delete(legacyPath + suffix);
                }
            }
        }
    }

    [Fact]
    public void AttendanceMatchIndex_MatchesLinearMatcherAndCanonicalizerIdentically()
    {
        var roster = new[]
        {
            Person("p1", "1", "유영인", "youngin@example.com"),
            Person("p2", "2", "김민수", "minsu1@example.com"),
            Person("p3", "3", "김민수", "minsu2@example.com"),
            Person("p4", "4", "박서준", "seojun@example.com")
        };
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NameNormalizer.Normalize("서준아이패드")] = "p4"
        };

        var linear = new AttendanceMatcher();
        var indexed = new AttendanceMatchIndex(roster, aliases);

        var cases = new (string Name, string? Email)[]
        {
            ("아무개", "youngin@example.com"),
            ("서준아이패드", null),
            ("김민수", null),
            ("영인 유", null),
            ("민수 김", null),
            ("박서준 iPhone", null),
            ("완전외부참가자", null)
        };

        foreach (var (name, email) in cases)
        {
            var expected = linear.Match(roster, aliases, name, email);
            var actual = indexed.Match(name, email);
            Assert.Equal(expected.Person?.Id, actual.Person?.Id);
            Assert.Equal(expected.Confidence, actual.Confidence);
            Assert.Equal(expected.Score, actual.Score);

            var expectedCanon = KoreanNameCanonicalizer.TryCanonicalize(name, roster, out var expName, out var expPerson);
            var actualCanon = indexed.TryCanonicalizeKoreanName(name, out var actName, out var actPerson);
            Assert.Equal(expectedCanon, actualCanon);
            Assert.Equal(expName, actName);
            Assert.Equal(expPerson?.Id, actPerson?.Id);
        }
    }

    [Fact]
    public async Task IngestRelaySnapshot_UpdatesSessionWithoutRebuildingBoard_AndBoardReflectsAttendees()
    {
        await _repository.ReplaceRosterAsync(new RosterImportResult(
            "r1", "test.xlsx", "test.xlsx", DateTimeOffset.UtcNow,
            new[] { Person("p1", "1", "유영인", "youngin@example.com") }));

        var bridge = new ZoomAppBridgeService(
            _service,
            Options.Create(new ZoomAppOptions()),
            TimeProvider.System);

        var ingestion = await bridge.IngestRelaySnapshotAsync(new ZoomRelaySnapshotPayload(
            "123 456 789",
            "uuid-meeting",
            "host",
            new[] { "getMeetingParticipants", "getMeetingContext", "getUserContext" },
            DateTimeOffset.UtcNow,
            new[] { new ZoomAppParticipantRequest("u1", "영인 유", "host") }));

        Assert.Equal("123456789", ingestion.MeetingId);
        Assert.Equal(1, ingestion.PresentCount);

        var board = await _service.BuildBoardAsync("123456789");
        Assert.Equal(AttendanceState.Present, Assert.Single(board.People).AttendanceState);
        Assert.Equal("유영인", Assert.Single(board.CurrentConnections!).DisplayName);
    }

    private static RosterPerson Person(string id, string sequence, string name, string email)
        => new(id, sequence, name, NameNormalizer.Normalize(name), email, string.Empty, string.Empty, Array.Empty<string>());
}
