using System.Data;
using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Roster;

public sealed class ExcelRosterParser
{
    private sealed record HeaderAliases(string[] Name, string[] Sequence, string[] Email,
        string[] Phone, string[] Organization, string[] Group);

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

        using var reader = ExcelReaderFactory.CreateReader(stream);
        var (table, headerRowIndex, columns, aliases) = FindRoster(reader.AsDataSet());
        var nameIndex = FindColumnIndex(columns, aliases.Name);
        var sequenceIndex = FindColumnIndex(columns, aliases.Sequence);
        var emailIndex = FindColumnIndex(columns, aliases.Email);
        var phoneIndex = FindColumnIndex(columns, aliases.Phone);
        var orgIndex = FindColumnIndex(columns, aliases.Organization);
        var groupIndices = aliases.Group.Where(columns.ContainsKey).Select(header => columns[header]).ToArray();

        var people = new List<RosterPerson>();

        for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var name = GetValue(row, nameIndex);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var email = GetValue(row, emailIndex).ToLowerInvariant();
            var phone = NormalizePhone(GetValue(row, phoneIndex));
            var organization = GetValue(row, orgIndex);
            var sequence = GetValue(row, sequenceIndex);
            var group = NormalizeWhitespace(GetGroupValue(row, groupIndices));
            var personAliases = BuildAliases(name, email, phone, organization);

            // The group is deliberately excluded from the id key: regrouping people between
            // imports must not orphan their attendance history.
            var personId = BuildStablePersonId(sequence, NameNormalizer.Normalize(name));
            if (people.Any(person => string.Equals(person.Id, personId, StringComparison.Ordinal)))
            {
                personId = BuildStablePersonId(sequence, $"{NameNormalizer.Normalize(name)}|{rowIndex}");
            }

            people.Add(new RosterPerson(
                Id: personId,
                Sequence: sequence,
                Name: name.Trim(),
                NormalizedName: NameNormalizer.Normalize(name),
                Email: email,
                Phone: phone,
                Organization: organization.Trim(),
                Aliases: personAliases,
                Group: group));
        }

        return new RosterImportResult(
            ImportId: Guid.NewGuid().ToString("N"),
            SourcePath: sourcePath,
            DisplayName: displayName,
            ImportedAt: DateTimeOffset.UtcNow,
            People: people);
    }

    private static (DataTable Table, int RowIndex, Dictionary<string, int> Columns, HeaderAliases Aliases) FindRoster(DataSet workbook)
    {
        foreach (var aliases in HeaderPriority)
        {
            foreach (DataTable table in workbook.Tables)
            {
                for (var rowIndex = 0; rowIndex < Math.Min(table.Rows.Count, 10); rowIndex++)
                {
                    var columns = BuildColumnMap(table.Rows[rowIndex]);
                    if (FindColumnIndex(columns, aliases.Sequence).HasValue && FindColumnIndex(columns, aliases.Name).HasValue)
                    {
                        return (table, rowIndex, columns, aliases);
                    }
                }
            }
        }

        throw new InvalidOperationException("Could not find a roster sheet containing valid name and ID headers (성명/번호, Name/ID, Nombre/DNI).");
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
