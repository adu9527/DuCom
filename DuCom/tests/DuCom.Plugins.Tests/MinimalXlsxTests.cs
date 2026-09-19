using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using DuCom.Plugins.Timer;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class MinimalXlsxTests
{
    [Fact]
    public void WorkbookContainsValidPartsWithLapRows()
    {
        TimerSession session = new()
        {
            Mode = StopwatchMode.Paused,
            AccumulatedMs = 90_000,
            Laps =
            [
                new LapRecord(1, 12_345, 12_345, 1_700_000_000_000),
                new LapRecord(2, 8_888, 21_233, 1_700_000_008_888),
            ],
        };

        byte[] bytes = MinimalXlsx.BuildLapWorkbook(session, DateTimeOffset.Now, chinese: true);
        Assert.True(bytes.Length > 500);

        using MemoryStream stream = new(bytes);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        string[] parts = [.. archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, System.StringComparer.Ordinal)];
        Assert.Contains("[Content_Types].xml", parts);
        Assert.Contains("_rels/.rels", parts);
        Assert.Contains("xl/workbook.xml", parts);
        Assert.Contains("xl/_rels/workbook.xml.rels", parts);
        Assert.Contains("xl/worksheets/sheet1.xml", parts);

        ZipArchiveEntry sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!;
        using StreamReader reader = new(sheet.Open());
        string xml = reader.ReadToEnd();
        Assert.Contains("序号", xml);
        Assert.Contains("单次时间", xml);
        Assert.Contains("12.345", xml);
        Assert.Contains("08.888", xml);
        Assert.Contains("21.233", xml);
        Assert.Contains("平均值", xml);
        Assert.Contains("最大值", xml);
        Assert.Contains("最小值", xml);
        Assert.Contains("10.617", xml);
        // Every row element must be well-formed and referenced (r="n" on row and cells).
        Assert.Equal(7, Regex.Count(xml, "<row r=\"\\d+\">"));
        Assert.DoesNotContain("&amp;#", xml);
    }

    [Fact]
    public void CsvContainsLapRowsAndStatistics()
    {
        TimerSession session = new()
        {
            Mode = StopwatchMode.Paused,
            AccumulatedMs = 4_000,
            Laps =
            [
                new LapRecord(1, 1_000, 1_000, 1_700_000_000_000),
                new LapRecord(2, 3_000, 4_000, 1_700_000_004_000),
            ],
        };

        string csv = System.Text.Encoding.UTF8.GetString(DuCom.Plugins.Timer.Plugin.BuildCsv(session, DateTimeOffset.Now, chinese: true));

        Assert.Contains("统计,平均值,最大值,最小值", csv);
        Assert.Contains(",02.000,03.000,01.000", csv);
    }
}
