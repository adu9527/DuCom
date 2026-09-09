using System.IO.Compression;
using System.Text;

namespace DuCom.Plugins.Timer;

/// <summary>
/// Minimal single-sheet .xlsx writer (spreadsheetml) with inline strings only: no third-party
/// dependency, opens directly in Excel/WPS. Rows are written as text cells so lap times keep
/// their millisecond display exactly as shown in the app.
/// </summary>
public static class MinimalXlsx
{
    public static byte[] BuildLapWorkbook(TimerSession session, DateTimeOffset exportedAt, bool chinese)
    {
        string indexHeader = chinese ? "序号" : "Index";
        string lapHeader = chinese ? "单次时间" : "Lap";
        string totalHeader = chinese ? "累计时间" : "Total";
        string clockHeader = chinese ? "记录时刻" : "Wall clock";
        string exportedLabel = chinese ? "导出时间" : "Exported";

        StringBuilder sheet = new();
        sheet.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cols><col min="1" max="1" width="8" customWidth="1"/><col min="2" max="3" width="15" customWidth="1"/><col min="4" max="4" width="22" customWidth="1"/></cols><sheetData>""");
        AppendRow(sheet, 1, [indexHeader, lapHeader, totalHeader, clockHeader]);
        int row = 2;
        foreach (LapRecord lap in session.Laps)
        {
            AppendRow(sheet, row++, [
                lap.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Format(lap.LapMs),
                Format(lap.TotalMs),
                $"{DateTimeOffset.FromUnixTimeMilliseconds(lap.WallClockUnixMs).ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}",
            ]);
        }

        AppendRow(sheet, row, [exportedLabel, "", "", $"{exportedAt:yyyy-MM-dd HH:mm:ss}"]);
        sheet.Append("</sheetData></worksheet>");

        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                <Default Extension="xml" ContentType="application/xml"/>
                <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """);
            AddEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{(chinese ? "秒表" : "Stopwatch")}" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            AddEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """);
            AddEntry(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
        }

        return buffer.ToArray();
    }

    private static void AppendRow(StringBuilder sheet, int rowNumber, string[] cells)
    {
        sheet.Append($"<row r=\"{rowNumber}\">");
        for (int index = 0; index < cells.Length; index++)
        {
            string reference = $"{(char)('A' + index)}{rowNumber}";
            sheet.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(cells[index])}</t></is></c>");
        }

        sheet.Append("</row>");
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string Format(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
