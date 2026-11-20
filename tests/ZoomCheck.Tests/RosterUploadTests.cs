using System.Text;
using System.Text.Json;
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

        var problem = AssertProblem(response, "ROSTER_UNSUPPORTED_EXTENSION", "roster_err_unsupported_extension");
        Assert.Equal(".txt", problem.Extensions["extension"]);
    }

    [Fact]
    public async Task Upload_RejectsEmptyFile_WithStructured400()
    {
        await using var stream = new MemoryStream();
        var formFile = new FormFile(stream, 0, 0, "file", "empty.xlsx");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        AssertProblem(response, "ROSTER_FILE_REQUIRED", "roster_err_file_required");
    }

    [Fact]
    public async Task Upload_RejectsNonExcelContents_With400Not500()
    {
        var bytes = Encoding.UTF8.GetBytes("this is plain text, not an excel workbook");
        await using var stream = new MemoryStream(bytes);
        var formFile = new FormFile(stream, 0, bytes.Length, "file", "roster-not-excel.xlsx");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        AssertProblem(response, "ROSTER_NOT_EXCEL", "roster_err_not_excel");
    }

    [Fact]
    public async Task Upload_RejectsCorruptZipBytes_With400Not500()
    {
        // ZIP signature followed by truncated garbage.
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0xFF, 0xFE };
        await using var stream = new MemoryStream(bytes);
        var formFile = new FormFile(stream, 0, bytes.Length, "file", "corrupt.xlsx");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        AssertProblem(response, "ROSTER_NOT_EXCEL", "roster_err_not_excel");
    }

    [Fact]
    public async Task Upload_RejectsEmptySheet_WithStructured400()
    {
        var path = MinimalXlsxBuilder.WriteWorkbook(
            _workDirectory,
            "empty-sheet.xlsx",
            "명단",
            Array.Empty<string?[]>());
        await using var stream = File.OpenRead(path);
        var formFile = new FormFile(stream, 0, stream.Length, "file", "empty-sheet.xlsx");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        AssertProblem(response, "ROSTER_EMPTY_SHEET", "roster_err_empty_sheet");
    }

    [Fact]
    public async Task Upload_RejectsMissingHeaders_WithFoundHeadersAndMissingColumns()
    {
        var path = MinimalXlsxBuilder.WriteWorkbook(
            _workDirectory,
            "bad-headers.xlsx",
            "명단",
            new List<string?[]>
            {
                new string?[] { "순번코드", "참가자명", "이메일" },
                new string?[] { "1", "학생001", "student001@example.com" }
            });
        await using var stream = File.OpenRead(path);
        var formFile = new FormFile(stream, 0, stream.Length, "file", "bad-headers.xlsx");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        var problem = AssertProblem(response, "ROSTER_MISSING_HEADERS", "roster_err_missing_headers");
        var foundHeaders = Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.Extensions["foundHeaders"]);
        var missingColumns = Assert.IsAssignableFrom<IReadOnlyList<string>>(problem.Extensions["missingColumns"]);
        Assert.Equal(new[] { "순번코드", "참가자명", "이메일" }, foundHeaders);
        Assert.Equal(new[] { "name", "sequence" }, missingColumns);
    }

    [Fact]
    public async Task Upload_RejectsHeaderOnlyWorkbook_WithNoRows400()
    {
        var path = MinimalXlsxBuilder.WriteWorkbook(
            _workDirectory,
            "no-rows.xlsx",
            "명단",
            new List<string?[]>
            {
                new string?[] { "번호", "성명", "이메일" }
            });
        await using var stream = File.OpenRead(path);
        var formFile = new FormFile(stream, 0, stream.Length, "file", "no-rows.xlsx");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        AssertProblem(response, "ROSTER_NO_ROWS", "roster_err_no_rows");
    }

    [Fact]
    public async Task Upload_ImportsDuplicateNumbers_WithStructuredWarning()
    {
        var path = MinimalXlsxBuilder.WriteWorkbook(
            _workDirectory,
            "duplicates.xlsx",
            "명단",
            new List<string?[]>
            {
                new string?[] { "번호", "성명", "이메일" },
                new string?[] { "5", "학생005", "student005@example.com" },
                new string?[] { "6", "학생006", "student006@example.com" },
                new string?[] { "5", "학생005", "student005@example.com" }
            });
        await using var stream = File.OpenRead(path);
        var formFile = new FormFile(stream, 0, stream.Length, "file", "duplicates.xlsx");

        var response = await _controller.Upload(formFile, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(3, document.RootElement.GetProperty("count").GetInt32());
        var duplicates = document.RootElement.GetProperty("warnings").GetProperty("duplicateNumbers");
        Assert.Equal(1, duplicates.GetArrayLength());
        var first = duplicates[0];
        Assert.Equal("5", first.GetProperty("sequence").GetString());
        Assert.Equal(2, first.GetProperty("count").GetInt32());
        Assert.Equal("학생005", first.GetProperty("names")[0].GetString());
        Assert.Equal(2, first.GetProperty("rowNumbers")[0].GetInt32());
        Assert.Equal(4, first.GetProperty("rowNumbers")[1].GetInt32());
    }

    private static ProblemDetails AssertProblem(IActionResult response, string expectedErrorCode, string expectedMessageKey)
    {
        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal(expectedErrorCode, problem.Extensions["errorCode"]);
        Assert.Equal(expectedMessageKey, problem.Extensions["messageKey"]);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        return problem;
    }
}
