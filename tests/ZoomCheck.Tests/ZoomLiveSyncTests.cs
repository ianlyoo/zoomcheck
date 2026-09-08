using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using ZoomCheck.Backend.Options;
using ZoomCheck.Backend.Services;
using ZoomCheck.Core.Enums;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

public sealed class ZoomLiveSyncTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"zoomcheck-zoom-api-{Guid.NewGuid():N}.db");

    private SqliteAttendanceRepository _repository = null!;
    private AttendanceApplicationService _attendance = null!;

    public async Task InitializeAsync()
    {
        _repository = new SqliteAttendanceRepository(_databasePath);
        await _repository.InitializeAsync();
        _attendance = new AttendanceApplicationService(
            new ExcelRosterParser(),
            _repository,
            new AttendanceMatcher());

        await _repository.ReplaceRosterAsync(new RosterImportResult(
            "roster",
            "test.xlsx",
            "test.xlsx",
            DateTimeOffset.UtcNow,
            new[]
            {
                Person("p1", "김영인", "youngin@example.com"),
                Person("p2", "이순신", "sunshin@example.com")
            }));
    }

    public Task DisposeAsync()
    {
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
    public async Task SyncAsync_PaginatesFiltersActiveParticipantsAndUsesEmailMatching()
    {
        var apiHandler = new QueueHttpMessageHandler(
            Json(HttpStatusCode.OK, """
                {
                  "next_page_token": "page-2",
                  "participants": [
                    { "id": "a", "user_name": "Youngin iPhone", "user_email": "youngin@example.com", "status": "in_meeting" },
                    { "id": "old", "user_name": "Already Left", "status": "not_in_meeting", "leave_time": "2026-08-30T01:00:00Z" }
                  ]
                }
                """),
            Json(HttpStatusCode.OK, """
                {
                  "next_page_token": "",
                  "participants": [
                    { "id": "b", "user_name": "이순신", "email": "sunshin@example.com", "status": "in_meeting" }
                  ]
                }
                """));
        var service = CreateSyncService(apiHandler);

        var result = await service.SyncAsync("123 456 789");

        Assert.Equal("123456789", result.MeetingId);
        Assert.Equal(3, result.ApiRecords);
        Assert.Equal(2, result.ActiveParticipants);
        Assert.Equal(1, result.IgnoredRecords);
        Assert.Equal(2, apiHandler.Requests.Count);
        Assert.Contains("next_page_token=page-2", apiHandler.Requests[1].Query);
        Assert.All(result.Snapshot.Board.People, person => Assert.Equal(AttendanceState.Present, person.AttendanceState));
        Assert.Empty(result.Snapshot.Board.UnmatchedParticipants);
    }

    [Fact]
    public async Task SyncAsync_DerivesLeftEventFromChangedLiveSnapshot()
    {
        var first = CreateSyncService(new QueueHttpMessageHandler(Json(HttpStatusCode.OK, """
            { "participants": [
              { "user_name": "김영인", "status": "in_meeting" },
              { "user_name": "이순신", "status": "in_meeting" }
            ] }
            """)));
        await first.SyncAsync("meeting-1");

        var second = CreateSyncService(new QueueHttpMessageHandler(Json(HttpStatusCode.OK, """
            { "participants": [
              { "user_name": "이순신", "status": "in_meeting" }
            ] }
            """)));
        var result = await second.SyncAsync("meeting-1");

        Assert.Equal(new[] { "김영인" }, result.Snapshot.LeftNames);
        Assert.Equal(AttendanceState.Left, result.Snapshot.Board.People.Single(person => person.RosterPersonId == "p1").AttendanceState);
        Assert.Equal(AttendanceState.Present, result.Snapshot.Board.People.Single(person => person.RosterPersonId == "p2").AttendanceState);
    }

    [Fact]
    public async Task SyncAsync_PreservesDistinctParticipantsWithSameDisplayName()
    {
        var service = CreateSyncService(new QueueHttpMessageHandler(Json(HttpStatusCode.OK, """
            { "participants": [
              { "id": "device-a", "user_name": "동명이인", "status": "in_meeting" },
              { "id": "device-b", "user_name": "동명이인", "status": "in_meeting" }
            ] }
            """)));

        var result = await service.SyncAsync("meeting-same-name");

        Assert.Equal(2, result.ActiveParticipants);
        Assert.Equal(2, result.Snapshot.PresentCount);
        Assert.Equal(2, result.Snapshot.JoinedNames.Count);
        Assert.Equal(2, result.Snapshot.Board.UnmatchedParticipants.Count);
    }

    [Fact]
    public async Task SyncAsync_EmptyResponseDoesNotOverwriteExistingAttendance()
    {
        await _attendance.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
            "meeting-1",
            new[] { "김영인" },
            ZoomLiveSyncService.SnapshotSource,
            DateTimeOffset.UtcNow));
        var service = CreateSyncService(new QueueHttpMessageHandler(Json(HttpStatusCode.OK, "{ \"participants\": [] }")));

        await Assert.ThrowsAsync<ZoomLiveSyncException>(() => service.SyncAsync("meeting-1"));

        var board = await _attendance.BuildBoardAsync("meeting-1");
        Assert.Equal(AttendanceState.Present, board.People.Single(person => person.RosterPersonId == "p1").AttendanceState);
    }

    [Fact]
    public async Task SyncAsync_ConfirmedEmptyResponseRecordsEveryoneAsLeft()
    {
        await _attendance.ApplyParticipantSnapshotAsync(new ParticipantSnapshotInput(
            "meeting-1",
            new[] { "김영인" },
            ZoomLiveSyncService.SnapshotSource,
            DateTimeOffset.UtcNow));
        var service = CreateSyncService(new QueueHttpMessageHandler(Json(HttpStatusCode.OK, "{ \"participants\": [] }")));

        var result = await service.SyncAsync("meeting-1", allowEmptySnapshot: true);

        Assert.Equal(0, result.ActiveParticipants);
        Assert.Equal(new[] { "김영인" }, result.Snapshot.LeftNames);
        Assert.Equal(AttendanceState.Left, result.Snapshot.Board.People.Single(person => person.RosterPersonId == "p1").AttendanceState);
    }

    [Fact]
    public async Task ZoomApiClient_PropagatesForbiddenWithoutLeakingBody()
    {
        var client = CreateApiClient(new QueueHttpMessageHandler(Json(
            HttpStatusCode.Forbidden,
            "{ \"message\": \"scope missing secret detail\" }")));

        var error = await Assert.ThrowsAsync<ZoomApiException>(() => client.GetLiveParticipantsAsync("meeting-1"));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.DoesNotContain("secret detail", error.Message);
    }

    [Fact]
    public async Task ZoomApiClient_StopsAtConfiguredPaginationLimit()
    {
        var handler = new QueueHttpMessageHandler(
            Json(HttpStatusCode.OK, "{ \"next_page_token\": \"two\", \"participants\": [] }"),
            Json(HttpStatusCode.OK, "{ \"next_page_token\": \"three\", \"participants\": [] }"));
        var client = CreateApiClient(handler, maxPages: 2);

        var error = await Assert.ThrowsAsync<ZoomApiException>(() => client.GetLiveParticipantsAsync("meeting-1"));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ZoomApiClient_RetriesOnceWhenZoomReturnsShortRetryAfter()
    {
        var rateLimited = Json(HttpStatusCode.TooManyRequests, "{ \"message\": \"slow down\" }");
        rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
        var handler = new QueueHttpMessageHandler(
            rateLimited,
            Json(HttpStatusCode.OK, "{ \"participants\": [] }"));
        var client = CreateApiClient(handler);

        var participants = await client.GetLiveParticipantsAsync("meeting-1");

        Assert.Empty(participants);
        Assert.Equal(2, handler.Requests.Count);
    }

    private ZoomLiveSyncService CreateSyncService(QueueHttpMessageHandler apiHandler)
        => new(CreateApiClient(apiHandler), _attendance);

    private static ZoomApiClient CreateApiClient(QueueHttpMessageHandler apiHandler, int maxPages = 20)
    {
        var zoomOptions = Options.Create(new ZoomOptions
        {
            AccountId = "test-account",
            ClientId = "test-client",
            ClientSecret = "test-secret"
        });
        var tokenHandler = new QueueHttpMessageHandler(Json(
            HttpStatusCode.OK,
            "{ \"access_token\": \"test-token\", \"expires_in\": 3600 }"));
        var tokenService = new ZoomOAuthTokenService(new HttpClient(tokenHandler), zoomOptions);
        var recoveryOptions = Options.Create(new ZoomRecoveryOptions
        {
            ParticipantsPageSize = 2,
            MaxParticipantPages = maxPages
        });
        return new ZoomApiClient(new HttpClient(apiHandler), tokenService, recoveryOptions);
    }

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
        => new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static RosterPerson Person(string id, string name, string email)
        => new(
            id,
            id == "p1" ? "1" : "2",
            name,
            NameNormalizer.Normalize(name),
            email,
            string.Empty,
            string.Empty,
            new[] { NameNormalizer.Normalize(name) });

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake HTTP response remains.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
