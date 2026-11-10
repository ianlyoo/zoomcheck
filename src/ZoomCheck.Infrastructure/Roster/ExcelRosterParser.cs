using System.Data;
using System.Security.Cryptography;
using System.Text;
using ExcelDataReader;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Roster;

public sealed class ExcelRosterParser
{
    /// <summary>
    /// Candidate header names in priority order across supported languages (Korean, English, Spanish).
    /// </summary>
    private static readonly string[] NameColumnNames =
    {
        "성명", "이름",
        "Name", "Full Name", "Student Name",
        "Nombre", "Nombre y Apellido", "Nombres", "Alumno", "Estudiante"
    };

    private static readonly string[] SequenceColumnNames =
    {
        "번호", "학번",
        "ID", "No", "No.", "Number", "Student ID",
        "DNI", "Legajo", "Número", "Numero", "Nro", "Nro.", "Matrícula", "Matricula"
    };

    private static readonly string[] EmailColumnNames =
    {
        "이메일", "Email", "E-mail", "Mail", "Correo", "Correo Electrónico", "Correo Electronico"
    };

    private static readonly string[] PhoneColumnNames =
    {
        "연락처", "전화번호", "Phone", "Telephone", "Mobile", "Teléfono", "Telefono", "Celular", "Móvil", "Movil", "Contacto"
    };

    private static readonly string[] OrganizationColumnNames =
    {
        "소속기관", "소속", "Organization", "Organisation", "Org", "Institution", "Company", "Institución", "Institucion", "Organización", "Organizacion", "Empresa", "Universidad"
    };

    /// <summary>
    /// Group header names in priority order. The first header present in the sheet wins, so a
    /// roster carrying both "조" and "반" is read as 조. Matching is exact on the trimmed header
    /// text, which keeps "분반" from being mistaken for "반".
    /// </summary>
    private static readonly string[] GroupColumnNames =
    {
        "조", "분반", "그룹", "팀", "반",
        "Group", "Team", "Class", "Section",
        "Comisión", "Comision", "Grupo", "Equipo", "División", "Division", "Curso", "Sección", "Seccion"
    };

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
        var dataSet = reader.AsDataSet();
        var table = dataSet.Tables.Cast<DataTable>().FirstOrDefault(IsRosterTable)
            ?? throw new InvalidOperationException("Could not find a roster sheet containing valid name and ID headers (성명/번호, Name/ID, Nombre/DNI).");

        var headerRowIndex = FindHeaderRowIndex(table);
        var columns = BuildColumnMap(table.Rows[headerRowIndex]);
        var nameIndex = FindColumnIndex(columns, NameColumnNames);
        var sequenceIndex = FindColumnIndex(columns, SequenceColumnNames);
        var emailIndex = FindColumnIndex(columns, EmailColumnNames);
        var phoneIndex = FindColumnIndex(columns, PhoneColumnNames);
        var orgIndex = FindColumnIndex(columns, OrganizationColumnNames);
        var groupIndex = FindColumnIndex(columns, GroupColumnNames);

        var people = new List<RosterPerson>();

        for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var name = nameIndex.HasValue ? row[nameIndex.Value]?.ToString()?.Trim() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var email = emailIndex.HasValue ? (row[emailIndex.Value]?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty) : string.Empty;
            var phone = phoneIndex.HasValue ? NormalizePhone(row[phoneIndex.Value]?.ToString()?.Trim() ?? string.Empty) : string.Empty;
            var organization = orgIndex.HasValue ? (row[orgIndex.Value]?.ToString()?.Trim() ?? string.Empty) : string.Empty;
            var sequence = sequenceIndex.HasValue ? (row[sequenceIndex.Value]?.ToString()?.Trim() ?? string.Empty) : string.Empty;
            var group = groupIndex.HasValue ? NormalizeWhitespace(row[groupIndex.Value]?.ToString()?.Trim() ?? string.Empty) : string.Empty;
            var aliases = BuildAliases(name, email, phone, organization);

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
                Aliases: aliases,
                Group: group));
        }

        return new RosterImportResult(
            ImportId: Guid.NewGuid().ToString("N"),
            SourcePath: sourcePath,
            DisplayName: displayName,
            ImportedAt: DateTimeOffset.UtcNow,
            People: people);
    }

    private static bool IsRosterTable(DataTable table)
    {
        for (var rowIndex = 0; rowIndex < Math.Min(table.Rows.Count, 10); rowIndex++)
        {
            if (IsRosterHeaderRow(table.Rows[rowIndex]))
            {
                return true;
            }
        }

        return false;
    }

    private static int FindHeaderRowIndex(DataTable table)
    {
        for (var rowIndex = 0; rowIndex < Math.Min(table.Rows.Count, 10); rowIndex++)
        {
            if (IsRosterHeaderRow(table.Rows[rowIndex]))
            {
                return rowIndex;
            }
        }

        throw new InvalidOperationException("Roster header row not found.");
    }

    private static bool IsRosterHeaderRow(DataRow row)
    {
        var values = row.ItemArray
            .Select(cell => cell?.ToString()?.Trim())
            .Where(cell => !string.IsNullOrWhiteSpace(cell))
            .ToArray();

        var hasSequence = values.Any(v => SequenceColumnNames.Any(seq => string.Equals(seq, v, StringComparison.OrdinalIgnoreCase)));
        var hasName = values.Any(v => NameColumnNames.Any(name => string.Equals(name, v, StringComparison.OrdinalIgnoreCase)));

        return hasSequence && hasName;
    }

    private static Dictionary<string, int> BuildColumnMap(DataRow headerRow)
    {
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
