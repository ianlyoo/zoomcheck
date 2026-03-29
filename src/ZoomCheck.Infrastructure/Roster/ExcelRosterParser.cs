using System.Data;
using System.Text;
using ExcelDataReader;
using ZoomCheck.Core.Models;
using ZoomCheck.Core.Services;

namespace ZoomCheck.Infrastructure.Roster;

public sealed class ExcelRosterParser
{
    public RosterImportResult Parse(string filePath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var dataSet = reader.AsDataSet();
        var table = dataSet.Tables.Cast<DataTable>().FirstOrDefault(IsRosterTable)
            ?? throw new InvalidOperationException("Could not find a roster sheet containing 성명 and 번호 headers.");

        var headerRowIndex = FindHeaderRowIndex(table);
        var columns = BuildColumnMap(table.Rows[headerRowIndex]);
        var people = new List<RosterPerson>();

        for (var rowIndex = headerRowIndex + 1; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            var name = GetValue(row, columns, "성명");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var email = GetValue(row, columns, "이메일").Trim().ToLowerInvariant();
            var phone = NormalizePhone(GetValue(row, columns, "연락처"));
            var organization = GetValue(row, columns, "소속기관");
            var sequence = GetValue(row, columns, "번호");
            var aliases = BuildAliases(name, email, phone, organization);

            people.Add(new RosterPerson(
                Id: Guid.NewGuid().ToString("N"),
                Sequence: sequence,
                Name: name.Trim(),
                NormalizedName: NameNormalizer.Normalize(name),
                Email: email,
                Phone: phone,
                Organization: organization.Trim(),
                Aliases: aliases));
        }

        return new RosterImportResult(
            ImportId: Guid.NewGuid().ToString("N"),
            SourcePath: Path.GetFullPath(filePath),
            DisplayName: Path.GetFileName(filePath),
            ImportedAt: DateTimeOffset.UtcNow,
            People: people);
    }

    private static bool IsRosterTable(DataTable table)
    {
        for (var rowIndex = 0; rowIndex < Math.Min(table.Rows.Count, 10); rowIndex++)
        {
            var values = table.Rows[rowIndex].ItemArray.Select(cell => cell?.ToString()?.Trim()).ToArray();
            if (values.Contains("번호") && values.Contains("성명"))
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
            var values = table.Rows[rowIndex].ItemArray.Select(cell => cell?.ToString()?.Trim()).ToArray();
            if (values.Contains("번호") && values.Contains("성명"))
            {
                return rowIndex;
            }
        }

        throw new InvalidOperationException("Roster header row not found.");
    }

    private static Dictionary<string, int> BuildColumnMap(DataRow headerRow)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
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

    private static string GetValue(DataRow row, IReadOnlyDictionary<string, int> columns, string columnName)
    {
        return columns.TryGetValue(columnName, out var index)
            ? row[index]?.ToString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static string NormalizePhone(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
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
