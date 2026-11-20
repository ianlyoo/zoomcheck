using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using ExcelDataReader;
using ExcelDataReader.Exceptions;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Roster;

public sealed class ExcelRosterParser
{
    private const int HeaderScanRowLimit = 10;
    private const int MaxReportedHeaders = 12;

    private sealed record HeaderAliases(string[] Name, string[] Sequence, string[] Email,
        string[] Phone, string[] Organization, string[] Group);

    private readonly record struct FirstSequenceOccurrence(string Sequence, string FirstName, int FirstRowNumber);

    private sealed class DuplicateSequenceTracker
    {
        public DuplicateSequenceTracker(string sequence, string firstName, int firstRowNumber)
        {
            Sequence = sequence;
            Count = 1;
            Names = new List<string> { firstName };
            RowNumbers = new List<int> { firstRowNumber };
        }

        public string Sequence { get; }
        public int Count { get; private set; }
        public List<string> Names { get; }
        public List<int> RowNumbers { get; }

        public void Add(string name, int rowNumber)
        {
            Count += 1;
            RowNumbers.Add(rowNumber);
            if (!Names.Contains(name, StringComparer.Ordinal))
            {
                Names.Add(name);
            }
        }
    }

    // A workbook with the original header pair uses only the original columns.
    // Search for that pair across all sheets before considering any extended aliases.
    private static readonly HeaderAliases OriginalHeaders = new(
        ["성명"], ["번호"], ["이메일"], ["연락처"], ["소속기관"], ["조", "분반", "그룹", "팀", "반"]);

    private static readonly HeaderAliases[] HeaderPriority =
    [
        OriginalHeaders,
        new(
            [.. OriginalHeaders.Name, "이름", "Student Name", "Full Name", "Nombre y Apellido", "Name", "Nombre", "Nombres", "Alumno", "Estudiante"],
            [.. OriginalHeaders.Sequence, "학번", "Student ID", "DNI", "Legajo", "Matrícula", "Matricula",
                "ID", "Number", "No", "No.", "Número", "Numero", "Nro", "Nro."],
            [.. OriginalHeaders.Email, "Email", "E-mail", "Mail", "Correo", "Correo Electrónico", "Correo Electronico"],
            [.. OriginalHeaders.Phone, "전화번호", "Phone", "Telephone", "Mobile", "Teléfono", "Telefono", "Celular", "Móvil", "Movil", "Contacto"],
            [.. OriginalHeaders.Organization, "소속", "Organization", "Organisation", "Org", "Institution", "Company",
                "Institución", "Institucion", "Organización", "Organizacion", "Empresa", "Universidad"],
            [.. OriginalHeaders.Group, "Group", "Team", "Class", "Section", "Comisión", "Comision", "Grupo", "Equipo",
                "División", "Division", "Curso", "Sección", "Seccion"])
    ];

    public RosterImportResult Parse(string filePath)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Parse(stream, Path.GetFullPath(filePath), Path.GetFileName(filePath));
    }

    public RosterImportResult Parse(Stream stream, string sourcePath, string displayName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("Roster stream must be readable.", nameof(stream));
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DataSet workbook;
        try
        {
            using var reader = ExcelReaderFactory.CreateReader(stream);
            workbook = reader.AsDataSet();
        }
        catch (InvalidPasswordException ex)
        {
            throw new RosterImportException(
                RosterImportErrorCode.PasswordProtected,
                "roster_err_password_protected",
                "암호로 보호된 Excel 파일은 읽을 수 없습니다. Excel에서 암호를 해제한 뒤 다시 올려 주세요.",
                innerException: ex);
        }
        catch (Exception ex) when (ex is ExcelReaderException or InvalidDataException or EndOfStreamException or FormatException or XmlException or ArgumentException)
        {
            throw new RosterImportException(
                RosterImportErrorCode.NotExcel,
                "roster_err_not_excel",
                "올바른 Excel 파일(.xlsx/.xls)이 아닙니다. Excel에서 통합 문서(.xlsx)로 다시 저장한 뒤 올려 주세요.",
                innerException: ex);
        }

        var (table, headerRowIndex, columns, aliases) = FindRoster(workbook);
        var nameIndex = FindColumnIndex(columns, aliases.Name);
        var sequenceIndex = FindColumnIndex(columns, aliases.Sequence);
        var emailIndex = FindColumnIndex(columns, aliases.Email);
        var phoneIndex = FindColumnIndex(columns, aliases.Phone);
        var orgIndex = FindColumnIndex(columns, aliases.Organization);
        var groupIndices = aliases.Group.Where(columns.ContainsKey).Select(header => columns[header]).ToArray();

        var people = new List<RosterPerson>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var firstSequences = new Dictionary<string, FirstSequenceOccurrence>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DuplicateSequenceTracker>? duplicateTrackers = null;
        List<DuplicateSequenceTracker>? duplicateOrder = null;

        for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var name = GetValue(row, nameIndex);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var trimmedName = name.Trim();
            var normalizedName = NameNormalizer.Normalize(name);
            var email = GetValue(row, emailIndex).ToLowerInvariant();
            var phone = NormalizePhone(GetValue(row, phoneIndex));
            var organization = GetValue(row, orgIndex);
            var sequence = GetValue(row, sequenceIndex);
            var group = NormalizeWhitespace(GetGroupValue(row, groupIndices));
            var personAliases = BuildAliases(name, email, phone, organization);

            if (sequence.Length > 0)
            {
                var excelRowNumber = rowIndex + 1;
                if (firstSequences.TryGetValue(sequence, out var first))
                {
                    duplicateTrackers ??= new Dictionary<string, DuplicateSequenceTracker>(StringComparer.OrdinalIgnoreCase);
                    duplicateOrder ??= new List<DuplicateSequenceTracker>();
                    if (!duplicateTrackers.TryGetValue(sequence, out var tracker))
                    {
                        tracker = new DuplicateSequenceTracker(first.Sequence, first.FirstName, first.FirstRowNumber);
                        duplicateTrackers[sequence] = tracker;
                        duplicateOrder.Add(tracker);
                    }

                    tracker.Add(trimmedName, excelRowNumber);
                }
                else
                {
                    firstSequences[sequence] = new FirstSequenceOccurrence(sequence, trimmedName, excelRowNumber);
                }
            }

            // The group is deliberately excluded from the id key: regrouping people between
            // imports must not orphan their attendance history.
            var personId = BuildStablePersonId(sequence, normalizedName);
            if (!seenIds.Add(personId))
            {
                personId = BuildStablePersonId(sequence, $"{normalizedName}|{rowIndex}");
                seenIds.Add(personId);
            }

            people.Add(new RosterPerson(
                Id: personId,
                Sequence: sequence,
                Name: trimmedName,
                NormalizedName: normalizedName,
                Email: email,
                Phone: phone,
                Organization: organization.Trim(),
                Aliases: personAliases,
                Group: group));
        }

        if (people.Count == 0)
        {
            throw new RosterImportException(
                RosterImportErrorCode.NoRosterRows,
                "roster_err_no_rows",
                "명단에 등록할 사람이 없습니다. 성명(이름) 열 아래에 참가자를 한 명 이상 입력한 뒤 다시 올려 주세요.",
                sheetName: table.TableName);
        }

        IReadOnlyList<RosterDuplicateNumberWarning> duplicateNumbers = duplicateOrder is null
            ? Array.Empty<RosterDuplicateNumberWarning>()
            : duplicateOrder
                .Select(tracker => new RosterDuplicateNumberWarning(
                    tracker.Sequence,
                    tracker.Count,
                    tracker.Names.ToArray(),
                    tracker.RowNumbers.ToArray()))
                .ToArray();

        return new RosterImportResult(
            ImportId: Guid.NewGuid().ToString("N"),
            SourcePath: sourcePath,
            DisplayName: displayName,
            ImportedAt: DateTimeOffset.UtcNow,
            People: people,
            DuplicateNumbers: duplicateNumbers);
    }

    private static (DataTable Table, int RowIndex, Dictionary<string, int> Columns, HeaderAliases Aliases) FindRoster(DataSet workbook)
    {
        foreach (var aliases in HeaderPriority)
        {
            foreach (DataTable table in workbook.Tables)
            {
                for (var rowIndex = 0; rowIndex < Math.Min(table.Rows.Count, HeaderScanRowLimit); rowIndex++)
                {
                    var columns = BuildColumnMap(table.Rows[rowIndex]);
                    if (FindColumnIndex(columns, aliases.Sequence).HasValue && FindColumnIndex(columns, aliases.Name).HasValue)
                    {
                        return (table, rowIndex, columns, aliases);
                    }
                }
            }
        }

        if (IsWorkbookEmpty(workbook))
        {
            throw new RosterImportException(
                RosterImportErrorCode.EmptySheet,
                "roster_err_empty_sheet",
                "시트가 비어 있습니다. 첫 10행 안에 번호·성명 머리글과 명단을 입력한 뒤 다시 올려 주세요.");
        }

        var (foundHeaders, missingColumns) = InspectCandidateHeaders(workbook);
        var foundSummary = foundHeaders.Count > 0
            ? $" 찾은 머리글: {string.Join(", ", foundHeaders)}."
            : string.Empty;

        throw new RosterImportException(
            RosterImportErrorCode.MissingHeaders,
            "roster_err_missing_headers",
            $"필수 열 머리글(성명·번호)을 찾지 못했습니다.{foundSummary} 첫 10행 안에 이름(성명·이름·Name)과 번호(번호·학번·ID·No) 열을 함께 넣어 주세요.",
            foundHeaders: foundHeaders,
            missingColumns: missingColumns);
    }

    private static bool IsWorkbookEmpty(DataSet workbook)
    {
        foreach (DataTable table in workbook.Tables)
        {
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                var row = table.Rows[rowIndex];
                for (var columnIndex = 0; columnIndex < row.ItemArray.Length; columnIndex++)
                {
                    if (!string.IsNullOrWhiteSpace(row[columnIndex]?.ToString()))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static (IReadOnlyList<string> FoundHeaders, IReadOnlyList<string> MissingColumns) InspectCandidateHeaders(DataSet workbook)
    {
        var allAliases = HeaderPriority[^1];
        List<string>? bestHeaders = null;
        var bestScore = -1;
        var bestHasName = false;
        var bestHasSequence = false;

        foreach (DataTable table in workbook.Tables)
        {
            for (var rowIndex = 0; rowIndex < Math.Min(table.Rows.Count, HeaderScanRowLimit); rowIndex++)
            {
                var columns = BuildColumnMap(table.Rows[rowIndex]);
                if (columns.Count == 0)
                {
                    continue;
                }

                var hasName = FindColumnIndex(columns, allAliases.Name).HasValue;
                var hasSequence = FindColumnIndex(columns, allAliases.Sequence).HasValue;
                var knownOptionalCount =
                    (FindColumnIndex(columns, allAliases.Email).HasValue ? 1 : 0) +
                    (FindColumnIndex(columns, allAliases.Phone).HasValue ? 1 : 0) +
                    (FindColumnIndex(columns, allAliases.Organization).HasValue ? 1 : 0) +
                    (allAliases.Group.Any(columns.ContainsKey) ? 1 : 0);

                // Prefer rows that already look like headers (match at least one known column) and have multiple columns.
                var score = ((hasName ? 1 : 0) + (hasSequence ? 1 : 0)) * 100 + knownOptionalCount * 20 + Math.Min(columns.Count, MaxReportedHeaders);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHasName = hasName;
                    bestHasSequence = hasSequence;
                    bestHeaders = columns
                        .OrderBy(pair => pair.Value)
                        .Select(pair => pair.Key)
                        .Take(MaxReportedHeaders)
                        .ToList();
                }
            }
        }

        var missing = new List<string>(2);
        if (!bestHasName)
        {
            missing.Add("name");
        }

        if (!bestHasSequence)
        {
            missing.Add("sequence");
        }

        return (bestHeaders?.ToArray() ?? Array.Empty<string>(), missing.ToArray());
    }

    private static Dictionary<string, int> BuildColumnMap(DataRow headerRow)
    {
        // Duplicate headings use the leftmost column after trimming and ignoring case.
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < headerRow.ItemArray.Length; index++)
        {
            var value = headerRow[index]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && !columns.ContainsKey(value))
            {
                columns[value] = index;
            }
        }

        return columns;
    }

    private static int? FindColumnIndex(IReadOnlyDictionary<string, int> columns, IEnumerable<string> candidateNames)
    {
        foreach (var candidate in candidateNames)
        {
            if (columns.TryGetValue(candidate, out var index))
            {
                return index;
            }
        }

        return null;
    }

    private static string GetValue(DataRow row, int? index) =>
        index.HasValue ? row[index.Value]?.ToString()?.Trim() ?? string.Empty : string.Empty;

    private static string GetGroupValue(DataRow row, IEnumerable<int> groupIndices)
    {
        foreach (var index in groupIndices)
        {
            var value = GetValue(row, index);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Trims and collapses internal whitespace runs to a single space so "1 조" and "1\u00a0\u00a0조"
    /// group together instead of splitting the board into look-alike groups.
    /// </summary>
    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string NormalizePhone(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static string BuildStablePersonId(string sequence, string normalizedName)
    {
        var key = $"sequence-name:{sequence.Trim()}|{normalizedName}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..32];
    }

    private static IReadOnlyList<string> BuildAliases(string name, string email, string phone, string organization)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NameNormalizer.Normalize(name)
        };

        if (!string.IsNullOrWhiteSpace(email))
        {
            aliases.Add(NameNormalizer.Normalize(email.Split('@')[0]));
        }

        if (!string.IsNullOrWhiteSpace(phone))
        {
            aliases.Add(phone);
        }

        if (!string.IsNullOrWhiteSpace(organization))
        {
            aliases.Add(NameNormalizer.Normalize($"{name}{organization}"));
        }

        return aliases.ToArray();
    }
}
