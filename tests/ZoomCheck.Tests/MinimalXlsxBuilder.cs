using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ZoomCheck.Tests;

/// <summary>
/// Writes a minimal, spec-valid .xlsx (SpreadsheetML) package using only BCL zip + XML APIs,
/// so roster parsing can be tested end-to-end without adding an Excel-writing dependency.
/// Cells are emitted as inline strings, which is enough for header/value detection.
/// </summary>
internal static class MinimalXlsxBuilder
{
    public static string WriteWorkbook(string directory, string fileName, string sheetName, IReadOnlyList<string?[]> rows) =>
        WriteWorkbook(directory, fileName, (sheetName, rows));

    public static string WriteWorkbook(string directory, string fileName, params (string Name, IReadOnlyList<string?[]> Rows)[] sheets)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var overrides = string.Concat(sheets.Select((_, i) =>
            $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\" />"));
        WriteEntry(archive, "[Content_Types].xml", ContentTypes(overrides));
        WriteEntry(archive, "_rels/.rels", RootRels);
        var workbookSheets = string.Concat(sheets.Select((sheet, i) =>
            $"<sheet name=\"{Escape(sheet.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\" />"));
        WriteEntry(archive, "xl/workbook.xml", Workbook(workbookSheets));
        var relationships = string.Concat(sheets.Select((_, i) =>
            $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\" />"));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRels(relationships));
        for (var index = 0; index < sheets.Length; index++)
        {
            WriteEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", Sheet(sheets[index].Rows));
        }

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string ContentTypes(string overrides) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
          <Default Extension="xml" ContentType="application/xml" />
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" />
          {overrides}
        </Types>
        """;

    private const string RootRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml" />
        </Relationships>
        """;

    private static string WorkbookRels(string relationships) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          {relationships}
        </Relationships>
        """;

    private static string Workbook(string sheets) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets>{sheets}</sheets>
        </workbook>
        """;

    private static string Sheet(IReadOnlyList<string?[]> rows)
    {
        var builder = new StringBuilder();
        builder.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        builder.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            builder.Append($"<row r=\"{rowNumber}\">");

            var cells = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < cells.Length; columnIndex++)
            {
                var value = cells[columnIndex];
                if (value is null)
                {
                    continue;
                }

                var reference = $"{ColumnName(columnIndex)}{rowNumber}";
                builder.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(value)}</t></is></c>");
            }

            builder.Append("</row>");
        }

        builder.Append("</sheetData></worksheet>");
        return builder.ToString();
    }

    private static string ColumnName(int zeroBasedIndex)
    {
        var name = string.Empty;
        var index = zeroBasedIndex;
        do
        {
            name = (char)('A' + (index % 26)) + name;
            index = (index / 26) - 1;
        }
        while (index >= 0);

        return name;
    }

    private static string Escape(string value) => new XmlDocument().CreateTextNode(value).OuterXml;
}
