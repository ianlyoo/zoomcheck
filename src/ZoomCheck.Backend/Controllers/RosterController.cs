using Microsoft.AspNetCore.Mvc;
using ZoomCheck.Backend.Contracts;
using ZoomCheck.Core.Models;
using ZoomCheck.Infrastructure.Roster;
using ZoomCheck.Infrastructure.Services;

namespace ZoomCheck.Backend.Controllers;

[ApiController]
[Route("api/roster")]
public sealed class RosterController : ControllerBase
{
    private const long MaxRosterFileBytes = 20 * 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx",
        ".xls"
    };

    private readonly AttendanceApplicationService _attendanceService;

    public RosterController(AttendanceApplicationService attendanceService)
    {
        _attendanceService = attendanceService;
    }

    [HttpGet]
    public async Task<IActionResult> GetRoster(CancellationToken cancellationToken)
    {
        var roster = await _attendanceService.GetRosterAsync(cancellationToken);
        return Ok(roster);
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRosterFileBytes)]
    public async Task<IActionResult> Upload([FromForm] IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return RosterProblem(
                errorCode: "ROSTER_FILE_REQUIRED",
                messageKey: "roster_err_file_required",
                title: "명단 파일이 필요합니다.",
                detail: "내용이 있는 Excel .xlsx 또는 .xls 파일을 선택해 주세요.");
        }

        if (file.Length > MaxRosterFileBytes)
        {
            return RosterProblem(
                errorCode: "ROSTER_FILE_TOO_LARGE",
                messageKey: "roster_err_file_too_large",
                title: "명단 파일이 너무 큽니다.",
                detail: "20MB 이하의 Excel 파일을 사용해 주세요.",
                details: new Dictionary<string, object?> { ["maxSizeMb"] = 20 });
        }

        var displayName = Path.GetFileName(file.FileName);
        var extension = Path.GetExtension(displayName);
        if (!AllowedExtensions.Contains(extension))
        {
            return RosterProblem(
                errorCode: "ROSTER_UNSUPPORTED_EXTENSION",
                messageKey: "roster_err_unsupported_extension",
                title: "지원하지 않는 파일 형식입니다.",
                detail: "Excel .xlsx 또는 .xls 파일만 업로드할 수 있습니다.",
                details: new Dictionary<string, object?> { ["extension"] = extension });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var imported = await _attendanceService.ImportRosterAsync(stream, displayName, cancellationToken);
            var duplicateNumbers = imported.DuplicateNumbers ?? Array.Empty<RosterDuplicateNumberWarning>();
            return Ok(new
            {
                imported.ImportId,
                imported.DisplayName,
                Count = imported.People.Count,
                imported.ImportedAt,
                SessionReset = false,
                Warnings = new
                {
                    DuplicateNumbers = duplicateNumbers.Select(item => new
                    {
                        item.Sequence,
                        item.Count,
                        item.Names,
                        item.RowNumbers
                    }).ToArray()
                }
            });
        }
        catch (RosterImportException ex)
        {
            var details = new Dictionary<string, object?>();
            if (ex.FoundHeaders.Count > 0)
            {
                details["foundHeaders"] = ex.FoundHeaders;
            }

            if (ex.MissingColumns.Count > 0)
            {
                details["missingColumns"] = ex.MissingColumns;
            }

            if (!string.IsNullOrWhiteSpace(ex.SheetName))
            {
                details["sheetName"] = ex.SheetName;
            }

            return RosterProblem(
                errorCode: ex.ErrorCodeName,
                messageKey: ex.MessageKey,
                title: TitleForError(ex.ErrorCode),
                detail: ex.Message,
                details: details);
        }
        catch (InvalidOperationException ex)
        {
            return RosterProblem(
                errorCode: "ROSTER_INVALID",
                messageKey: "roster_err_invalid",
                title: "명단을 읽을 수 없습니다.",
                detail: ex.Message);
        }
        catch (IOException)
        {
            return RosterProblem(
                errorCode: "ROSTER_READ_FAILED",
                messageKey: "roster_err_read_failed",
                title: "명단 파일을 열 수 없습니다.",
                detail: "파일이 잠겨 있거나 손상되지 않았는지 확인한 뒤 다시 시도해 주세요.");
        }
    }

    [HttpPost("alias")]
    public async Task<IActionResult> SaveAlias([FromBody] AliasRequest request, CancellationToken cancellationToken)
    {
        await _attendanceService.SaveAliasAsync(request.Alias, request.RosterPersonId, request.Note, cancellationToken);
        return Ok(new { message = "Alias saved." });
    }

    private static string TitleForError(RosterImportErrorCode errorCode) => errorCode switch
    {
        RosterImportErrorCode.NotExcel => "Excel 파일이 아닙니다.",
        RosterImportErrorCode.PasswordProtected => "암호가 걸린 Excel 파일입니다.",
        RosterImportErrorCode.EmptySheet => "시트가 비어 있습니다.",
        RosterImportErrorCode.MissingHeaders => "필수 열 머리글을 찾지 못했습니다.",
        RosterImportErrorCode.NoRosterRows => "명단에 등록할 사람이 없습니다.",
        _ => "명단을 읽을 수 없습니다."
    };

    private static ObjectResult RosterProblem(
        string errorCode,
        string messageKey,
        string title,
        string detail,
        IDictionary<string, object?>? details = null)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = title,
            Detail = detail
        };
        problem.Extensions["errorCode"] = errorCode;
        problem.Extensions["messageKey"] = messageKey;
        if (details is { Count: > 0 })
        {
            foreach (var (key, value) in details)
            {
                if (value is not null)
                {
                    problem.Extensions[key] = value;
                }
            }
        }

        var result = new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status400BadRequest
        };
        result.ContentTypes.Add("application/problem+json");
        return result;
    }
}
