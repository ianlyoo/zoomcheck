namespace ZoomCheck.Infrastructure.Roster;

public enum RosterImportErrorCode
{
    NotExcel,
    PasswordProtected,
    EmptySheet,
    MissingHeaders,
    NoRosterRows
}

public sealed class RosterImportException : InvalidOperationException
{
    public RosterImportException(
        RosterImportErrorCode errorCode,
        string messageKey,
        string message,
        IReadOnlyList<string>? foundHeaders = null,
        IReadOnlyList<string>? missingColumns = null,
        string? sheetName = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        MessageKey = messageKey;
        FoundHeaders = foundHeaders ?? Array.Empty<string>();
        MissingColumns = missingColumns ?? Array.Empty<string>();
        SheetName = sheetName;
    }

    public RosterImportErrorCode ErrorCode { get; }

    public string MessageKey { get; }

    public IReadOnlyList<string> FoundHeaders { get; }

    public IReadOnlyList<string> MissingColumns { get; }

    public string? SheetName { get; }

    public string ErrorCodeName => ErrorCode switch
    {
        RosterImportErrorCode.NotExcel => "ROSTER_NOT_EXCEL",
        RosterImportErrorCode.PasswordProtected => "ROSTER_PASSWORD_PROTECTED",
        RosterImportErrorCode.EmptySheet => "ROSTER_EMPTY_SHEET",
        RosterImportErrorCode.MissingHeaders => "ROSTER_MISSING_HEADERS",
        RosterImportErrorCode.NoRosterRows => "ROSTER_NO_ROWS",
        _ => "ROSTER_INVALID"
    };
}
