using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Controllers;
using ZoomCheck.Core.Services;
using ZoomCheck.Infrastructure.Persistence;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Tests;

public sealed class RosterUploadTests : IAsyncLifetime
{
    private readonly string _workDirectory = Path.Combine(
        Path.GetTempPath(),
        $"zoomcheck-upload-{Guid.NewGuid():N}");
    private SqliteAttendanceRepository _repository = null!;
    private RosterController _controller = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_workDirectory);
        _repository = new SqliteAttendanceRepository(Path.Combine(_workDirectory, "test.db"));
        await _repository.InitializeAsync();
        _controller = new RosterController(new AttendanceApplicationService(
            new ExcelRosterParser(),
            _repository,
            new AttendanceMatcher()));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_workDirectory))
        {
            Directory.Delete(_workDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Upload_ImportsMultipartExcelStream()
    {
        var path = MinimalXlsxBuilder.WriteWorkbook(
            _workDirectory,
            "roster.xlsx",
            "명단",
            new List<string?[]>
            {
                new string?[] { "번호", "성명", "이메일", "연락처", "소속기관" },
                new string?[] { "1", "김영인", "youngin@example.com", "", "테스트" },
                new string?[] { "2", "이순신", "sunshin@example.com", "", "테스트" }
            });
        await using var stream = File.OpenRead(path);
        var formFile = new FormFile(stream, 0, stream.Length, "file", "roster.xlsx");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response);
        var roster = await _repository.GetRosterPeopleAsync();
        Assert.Equal(2, roster.Count);
    }

    [Fact]
    public async Task Upload_RejectsNonExcelExtension()
    {
        await using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var formFile = new FormFile(stream, 0, stream.Length, "file", "roster.txt");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }
}
