using System.IO.Compression;
using System.Globalization;
using System.IO;
using System.Text;

namespace DuCom.Services.Plugins;

/// <summary>Report header information rendered above the data table.</summary>
public sealed record ProductionExportContext(string ProjectName, string ToolVersion);

/// <summary>
/// Converts the production round CSV (时间,轮次,通道,测试项,结果,状态,本轮结果[,串口,耗时秒] —
/// one row per test item) into an Excel report with ONE ROW PER TEST (round): a
/// title/meta/statistics block, then one row per round where each test item spans
/// left/right columns (single-channel items collapse to one unsuffixed column),
/// per-ear verdicts, the pair verdict, ports, timing, red failure highlighting,
/// a frozen header, and fitted column widths.
/// </summary>
public static class ProductionRecordExporter
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    private sealed class TestGroup(int file, string serial, string round)
    {
        public int File { get; } = file;
        public string Serial { get; } = serial;
        public string Round { get; } = round;
        public string DisplayNumber { get; set; } = "";
        public DateTime? Started { get; set; }
        public DateTime? Ended { get; set; }
        public string PortLeft { get; set; } = "—";
        public string PortRight { get; set; } = "—";
        public string RoundResult { get; set; } = "";
        // (item, channel) → measured value and pass state; channel 0 = left, 1 = right.
        public Dictionary<(string Item, int Channel), (string Value, string State)> Cells { get; } = new();
        public string EarResult(int channel)
        {
            (string, string)[] mine = Cells.Where(pair => pair.Key.Channel == channel).Select(pair => pair.Value).ToArray();
            return mine.Length == 0 ? "—"
                : mine.All(cell => string.Equals(cell.Item2, "通过", StringComparison.Ordinal)) ? "通过" : "不通过";
        }
    }

    public static void Export(string csvPath, string xlsxPath, IReadOnlyList<string> itemOrder, ProductionExportContext? context = null) =>
        Export([csvPath], xlsxPath, itemOrder, context);

    /// <summary>Merges every record file (oldest first) into one report: one row per test.</summary>
    public static void Export(IReadOnlyList<string> csvPaths, string xlsxPath, IReadOnlyList<string> itemOrder, ProductionExportContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(csvPaths);
        ArgumentException.ThrowIfNullOrEmpty(xlsxPath);
        if (csvPaths.Count == 0) throw new InvalidDataException("产测记录为空，无法导出。");
        // Each file carries its own header row; worker restarts also reset the serial
        // counter, so the file index is part of the run identity below.
        List<(int File, string[] Row)> data = [];
        for (int file = 0; file < csvPaths.Count; file++)
        {
            ArgumentException.ThrowIfNullOrEmpty(csvPaths[file]);
            foreach (List<string> fields in File.ReadAllLines(csvPaths[file], Encoding.UTF8).Select(ParseCsvLine).Skip(1))
            {
                if (fields.Count >= 7) data.Add((file, fields.ToArray()));
            }
        }
        if (data.Count == 0) throw new InvalidDataException("产测记录为空，无法导出。");
        List<string[]> allRows = data.Select(entry => entry.Row).ToList();

        static int ChannelIndex(string channel) => channel == "左耳" ? 0 : 1;

        // One output row per test execution (serial) and round within it. Keys are
        // taken from the file, not from consecutive runs: the producer writes items
        // command-major (L,R,L,R…), and "once" runs restart the round counter at 1
        // every time — only the monotonic 测试号 separates consecutive single tests.
        List<TestGroup> groups = [];
        Dictionary<(int File, string Serial, string Round), TestGroup> byKey = new();
        foreach ((int file, string[] row) in data)
        {
            string serial = row.Length > 9 && row[9].Length > 0 ? row[9] : "—";
            if (!byKey.TryGetValue((file, serial, row[1]), out TestGroup? group))
            {
                group = new TestGroup(file, serial, row[1]);
                byKey[(file, serial, row[1])] = group;
                groups.Add(group);
            }
            if (TryParseTime(row[0], out DateTime at))
            {
                group.Started = group.Started is null || at < group.Started ? at : group.Started;
                group.Ended = group.Ended is null || at > group.Ended ? at : group.Ended;
            }
            int channel = ChannelIndex(row[2]);
            if (row.Length > 7 && row[7].Length > 0)
            {
                if (channel == 0) group.PortLeft = row[7]; else group.PortRight = row[7];
            }
            group.Cells[(row[3], channel)] = (row[4], row[5]);
            group.RoundResult = row[6];
        }
        // Global run numbering across all files: each new (file, serial) run gets the
        // next number, so a merged export reads 1..N regardless of session restarts.
        int runNumber = 0;
        (int File, string Serial)? lastRun = null;
        foreach (TestGroup group in groups)
        {
            (int, string) current = (group.File, group.Serial);
            if (lastRun != current) { runNumber++; lastRun = current; }
            group.DisplayNumber = runNumber.ToString(CultureInfo.InvariantCulture);
        }

        // Item columns: an item measured on both ears gets ·左/·右 columns; an item
        // that only ever ran on one ear collapses to a single unsuffixed column.
        bool anyLeft = allRows.Any(row => row[2] == "左耳");
        bool anyRight = allRows.Any(row => row[2] == "右耳");
        List<string> itemNames = [];
        foreach (string name in itemOrder)
        {
            if (allRows.Any(row => string.Equals(row[3], name, StringComparison.Ordinal)) && !itemNames.Contains(name, StringComparer.Ordinal))
            {
                itemNames.Add(name);
            }
        }
        foreach (string[] row in allRows)
        {
            if (!itemNames.Contains(row[3], StringComparer.Ordinal)) itemNames.Add(row[3]);
        }
        List<(string Item, int Channel)> itemColumns = [];
        foreach (string name in itemNames)
        {
            bool left = anyLeft && allRows.Any(row => row[3] == name && row[2] == "左耳");
            bool right = anyRight && allRows.Any(row => row[3] == name && row[2] == "右耳");
            if (left) itemColumns.Add((name, 0));
            if (right) itemColumns.Add((name, 1));
            if (!left && !right) itemColumns.Add((name, 0)); // unseen items keep one placeholder column
        }
        static string Suffix(string item, int channel, bool both) => both ? item + (channel == 0 ? "·左" : "·右") : item;
        bool bothChannels = anyLeft && anyRight;

        string[] header =
        [
            "测试号", "轮次", "开始时间", "结束时间", "耗时(s)",
            .. bothChannels ? (string[])["串口·左", "串口·右"] : ["串口"],
            .. itemColumns.Select(column => Suffix(column.Item, column.Channel, bothChannels)),
            .. bothChannels ? (string[])["左耳结果", "右耳结果"] : ["单耳结果"],
            "整对结果",
        ];

        int total = groups.Count;
        int passed = groups.Count(group => string.Equals(group.RoundResult, "通过", StringComparison.Ordinal));
        int failed = total - passed;
        string rate = total == 0 ? "—" : (100.0 * passed / total).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        List<string[]> sheet =
        [
            [$"产测记录 · {(context is { ProjectName.Length: > 0 } info ? info.ProjectName : "未命名项目")}"],
            [$"导出时间：{DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture)}　　工具版本：{context?.ToolVersion ?? "—"}　　测试项：{itemColumns.Count} 列 · 每次测试一行"],
            [$"测试总数 {total}　通过 {passed}　不通过 {failed}　通过率 {rate}"],
            [],
            header,
            .. groups.Select(group => (string[])[
                group.DisplayNumber,
                group.Round,
                group.Started?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? "—",
                group.Ended?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? "—",
                group.Started is { } start && group.Ended is { } end
                    ? Math.Max(0, (end - start).TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) : "—",
                .. bothChannels ? (string[])[group.PortLeft, group.PortRight] : [anyLeft ? group.PortLeft : group.PortRight],
                .. itemColumns.Select(column => group.Cells.TryGetValue((column.Item, column.Channel), out (string Value, string State) cell) ? cell.Value : "—"),
                .. bothChannels ? (string[])[group.EarResult(0), group.EarResult(1)] : [group.EarResult(anyLeft ? 0 : 1)],
                group.RoundResult,
            ]),
        ];

        // Cell styles: 0 Normal, 1 bold title, 2 gray meta, 3 bold header on gray,
        // 4 dark-red on light-red for failures.
        const int TitleStyle = 1, MetaStyle = 2, HeaderStyle = 3, FailStyle = 4;
        const int HeaderRowIndex = 4;
        List<int[]> styles = sheet.Select((row, index) =>
        {
            int[] rowStyles = new int[row.Length];
            if (index == 0) Array.Fill(rowStyles, TitleStyle);
            else if (index is 1 or 2) Array.Fill(rowStyles, MetaStyle);
            else if (index == HeaderRowIndex) Array.Fill(rowStyles, HeaderStyle);
            return rowStyles;
        }).ToList();
        foreach ((TestGroup group, int index) in groups.Select((group, index) => (group, index)))
        {
            int[] rowStyles = styles[HeaderRowIndex + 1 + index];
            for (int column = 0; column < header.Length; column++)
            {
                string title = header[column];
                int itemColumn = -1;
                if (column >= 5 + (bothChannels ? 2 : 1) && column < header.Length - (bothChannels ? 3 : 2))
                {
                    itemColumn = column - (5 + (bothChannels ? 2 : 1));
                }
                bool fail;
                if (itemColumn >= 0)
                {
                    (string item, int channel) = itemColumns[itemColumn];
                    fail = group.Cells.TryGetValue((item, channel), out (string Value, string State) cell)
                        && string.Equals(cell.State, "不通过", StringComparison.Ordinal);
                }
                else
                {
                    fail = string.Equals(Cell(sheet, HeaderRowIndex + 1 + index, column), "不通过", StringComparison.Ordinal);
                }
                if (fail) rowStyles[column] = FailStyle;
            }
        }

        int columnCount = header.Length;
        double[] widths = new double[columnCount];
        Array.Fill(widths, 7);
        for (int row = HeaderRowIndex; row < sheet.Count; row++)
        {
            for (int column = 0; column < sheet[row].Length && column < columnCount; column++)
            {
                widths[column] = Math.Max(widths[column], Math.Min(55, DisplayWidth(sheet[row][column]) + 3));
            }
        }
        // Meta rows live in column A; keep them from stretching the 轮次 column.
        widths[0] = Math.Min(widths[0], 12);

        WriteXlsx(xlsxPath, "产测记录", sheet, styles, freezeRows: HeaderRowIndex + 1, columnWidths: widths);
    }

    private static string Cell(List<string[]> sheet, int row, int column) =>
        column < sheet[row].Length ? sheet[row][column] : "";

    private static bool TryParseTime(string text, out DateTime time) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time);

    /// <summary>Rough spreadsheet width: CJK/full-width characters count double.</summary>
    private static double DisplayWidth(string value)
    {
        double width = 0;
        foreach (char character in value) width += character > 0x2E7F ? 2 : 1;
        return width;
    }

    /// <summary>Minimal RFC-4180 parser: quoted fields, doubled quotes, CRLF inside quotes.</summary>
    public static List<string> ParseCsvLine(string line)
    {
        List<string> fields = [];
        StringBuilder current = new();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"') { current.Append('"'); index++; }
                    else quoted = false;
                }
                else current.Append(character);
            }
            else if (character == '"') quoted = true;
            else if (character == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(character);
        }
        fields.Add(current.ToString());
        return fields;
    }

    internal static void WriteXlsx(string path, string sheetName, IReadOnlyList<string[]> rows) =>
        WriteXlsx(path, sheetName, rows, null, 0, null);

    internal static void WriteXlsx(
        string path,
        string sheetName,
        IReadOnlyList<string[]> rows,
        IReadOnlyList<int[]>? cellStyles,
        int freezeRows,
        IReadOnlyList<double>? columnWidths)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(sheetName);
        ArgumentNullException.ThrowIfNull(rows);
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        ZipArchiveEntry types = archive.CreateEntry("[Content_Types].xml");
        using (TextWriter writer = new StreamWriter(types.Open(), new UTF8Encoding(false)))
        {
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
                """);
        }
        ZipArchiveEntry rels = archive.CreateEntry("_rels/.rels");
        using (TextWriter writer = new StreamWriter(rels.Open(), new UTF8Encoding(false)))
        {
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
        }
        ZipArchiveEntry workbook = archive.CreateEntry("xl/workbook.xml");
        using (TextWriter writer = new StreamWriter(workbook.Open(), new UTF8Encoding(false)))
        {
            writer.Write($"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(TruncateSheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
        }
        ZipArchiveEntry workbookRels = archive.CreateEntry("xl/_rels/workbook.xml.rels");
        using (TextWriter writer = new StreamWriter(workbookRels.Open(), new UTF8Encoding(false)))
        {
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
                """);
        }
        ZipArchiveEntry styles = archive.CreateEntry("xl/styles.xml");
        using (TextWriter writer = new StreamWriter(styles.Open(), new UTF8Encoding(false)))
        {
            // xf indices used by cells: 0 Normal, 1 bold title, 2 gray meta text,
            // 3 bold header on gray fill, 4 dark-red text on light-red fill (failures).
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="5"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="14"/><name val="Calibri"/></font><font><sz val="11"/><color rgb="FF595959"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font><font><sz val="11"/><color rgb="FF9C0006"/><name val="Calibri"/></font></fonts><fills count="4"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFD9D9D9"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFC7CE"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="5"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="0" fontId="3" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"/><xf numFmtId="0" fontId="4" fillId="3" borderId="0" xfId="0" applyFont="1" applyFill="1"/></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
                """);
        }
        ZipArchiveEntry sheet = archive.CreateEntry("xl/worksheets/sheet1.xml");
        using (TextWriter writer = new StreamWriter(sheet.Open(), new UTF8Encoding(false)))
        {
            writer.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
            writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (freezeRows > 0)
            {
                writer.Write($"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{freezeRows}\" topLeftCell=\"A{freezeRows + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            }
            if (columnWidths is { Count: > 0 })
            {
                writer.Write("<cols>");
                for (int column = 0; column < columnWidths.Count; column++)
                {
                    writer.Write($"<col min=\"{column + 1}\" max=\"{column + 1}\" width=\"{columnWidths[column].ToString("0.#", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
                }
                writer.Write("</cols>");
            }
            writer.Write("<sheetData>");
            for (int row = 0; row < rows.Count; row++)
            {
                writer.Write($"<row r=\"{row + 1}\">");
                for (int column = 0; column < rows[row].Length; column++)
                {
                    string value = rows[row][column];
                    if (value.Length == 0) continue;
                    int style = cellStyles is not null && row < cellStyles.Count && column < cellStyles[row].Length ? cellStyles[row][column] : 0;
                    string styleAttribute = style == 0 ? "" : $" s=\"{style}\"";
                    writer.Write($"<c r=\"{ColumnName(column)}{row + 1}\"{styleAttribute} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(value)}</t></is></c>");
                }
                writer.Write("</row>");
            }
            writer.Write("</sheetData></worksheet>");
        }
    }

    private static string TruncateSheetName(string name) => name.Length <= 31 ? name : name[..31];

    public static string ColumnName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        StringBuilder name = new();
        for (int remainder = index; ; remainder /= 26)
        {
            name.Insert(0, (char)('A' + remainder % 26));
            if (remainder < 26) return name.ToString();
            remainder--;
        }
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
