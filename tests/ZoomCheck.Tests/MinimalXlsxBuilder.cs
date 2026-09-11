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
    public static string WriteWorkbook(string directory, string fileName, string sheetName, IReadOnlyList<string?[]> rows)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(archive, "[Content_Types].xml", ContentTypes);
        WriteEntry(archive, "_rels/.rels", RootRels);
        WriteEntry(archive, "xl/workbook.xml", Workbook(sheetName));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRels);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", Sheet(rows));

        return path;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
          <Default Extension="xml" ContentType="application/xml" />
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml" />
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml" />
        </Types>
        """;

    private const string RootRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml" />
        </Relationships>
        """;

    private const string WorkbookRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml" />
        </Relationships>
        """;

    private static string Workbook(string sheetName) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets><sheet name="{Escape(sheetName)}" sheetId="1" r:id="rId1" /></sheets>
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
