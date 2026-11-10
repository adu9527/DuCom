using System.IO.Compression;
using System.Text;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class ProductionRecordExporterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ducom-record-export-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EachTestRoundOccupiesExactlyOneRowWithLeftRightItemColumns()
    {
        // Command-major order (L,R,L,R per command) is what the plugin writes; the
        // export must still produce one row per round, not one row per item chunk.
        string csv = string.Join("\r\n",
            "时间,轮次,通道,测试项,结果,状态,本轮结果",
            "\"2026-11-07T15:00:00\",\"1\",\"左耳\",\"读取MAC地址\",\"11:22:33:44:55:66\",\"通过\",\"不通过\"",
            "\"2026-11-07T15:00:00\",\"1\",\"左耳\",\"电池电压\",\"3,957 mV\",\"通过\",\"不通过\"",
            "\"2026-11-07T15:00:02\",\"1\",\"右耳\",\"读取MAC地址\",\"AA:BB:CC:DD:EE:FF\",\"通过\",\"不通过\"",
            "\"2026-11-07T15:00:02\",\"1\",\"右耳\",\"电池电压\",\"0 mV\",\"不通过\",\"不通过\"",
            "\"2026-11-07T15:01:00\",\"2\",\"左耳\",\"读取MAC地址\",\"11:22:33:44:55:66\",\"通过\",\"通过\"",
            "\"2026-11-07T15:01:00\",\"2\",\"左耳\",\"电池电压\",\"3,957 mV\",\"通过\",\"通过\"");
        Directory.CreateDirectory(_path);
        string csvPath = Path.Combine(_path, "record.csv");
        File.WriteAllText(csvPath, csv, new UTF8Encoding(true));
        string xlsxPath = Path.Combine(_path, "record.xlsx");

        ProductionRecordExporter.Export(csvPath, xlsxPath, ["读取MAC地址", "电池电压"]);

        string sheet = ReadSheet(xlsxPath);
        // Report block above the table, statistics per test (round).
        Assert.Contains("产测记录 · 未命名项目", sheet);
        Assert.Contains("测试总数 2　通过 1　不通过 1　通过率 50.0%", sheet);
        // Items measured on both ears get ·左/·右 columns in one row per round.
        Assert.Contains("读取MAC地址·左", sheet);
        Assert.Contains("读取MAC地址·右", sheet);
        Assert.Contains("电池电压·右", sheet);
        Assert.Contains("左耳结果", sheet);
        Assert.Contains("右耳结果", sheet);
        Assert.Contains("整对结果", sheet);
        Assert.Contains("11:22:33:44:55:66", sheet);
        Assert.Contains("0 mV", sheet);
        // Header is row 5; two rounds → exactly two data rows.
        Assert.Contains("<row r=\"5\">", sheet, StringComparison.Ordinal);
        Assert.Contains("<row r=\"7\">", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<row r=\"8\">", sheet, StringComparison.Ordinal);
        Assert.Matches("name=\"产测记录\"", ReadEntry(xlsxPath, "xl/workbook.xml"));
    }

    [Fact]
    public void SingleEarRecordsCollapseToUnsuffixedColumns()
    {
        string csv = string.Join("\r\n",
            "时间,轮次,通道,测试项,结果,状态,本轮结果,串口,耗时秒",
            "\"2026-11-10T09:44:20.100+08:00\",\"1\",\"左耳\",\"读取MAC地址\",\"64:65:48:F3:4D:05\",\"通过\",\"通过\",\"COM42\",\"4.2\"",
            "\"2026-11-10T09:44:21.900+08:00\",\"1\",\"左耳\",\"获取电池电量\",\"60%\",\"通过\",\"通过\",\"COM42\",\"4.2\"");
        Directory.CreateDirectory(_path);
        string csvPath = Path.Combine(_path, "record.csv");
        File.WriteAllText(csvPath, csv, new UTF8Encoding(true));
        string xlsxPath = Path.Combine(_path, "record.xlsx");

        ProductionRecordExporter.Export(csvPath, xlsxPath, ["读取MAC地址", "获取电池电量"]);

        string sheet = ReadSheet(xlsxPath);
        // One ear in the whole file → no ·左/·右 suffixes, single verdict column.
        Assert.DoesNotContain("·左", sheet);
        Assert.DoesNotContain("·右", sheet);
        Assert.Contains("单耳结果", sheet);
        Assert.Contains("COM42", sheet);
        Assert.Contains("2026-11-10 09:44:20", sheet);
        Assert.Contains("2026-11-10 09:44:21", sheet);
        Assert.Contains("1.8", sheet); // round duration derived from start/end
        Assert.Contains("测试总数 1　通过 1　不通过 0　通过率 100.0%", sheet);
    }

    [Fact]
    public void ExtendedPairRecordShowsPortsVerdictsAndFailureStylingInOneRow()
    {
        string csv = string.Join("\r\n",
            "时间,轮次,通道,测试项,结果,状态,本轮结果,串口,耗时秒,测试号",
            "\"2026-11-10T09:44:20.100+08:00\",\"3\",\"左耳\",\"读取MAC地址\",\"64:65:48:F3:4D:05\",\"通过\",\"不通过\",\"COM42\",\"4.2\",\"1\"",
            "\"2026-11-10T09:44:23.300+08:00\",\"3\",\"左耳\",\"获取电池电量\",\"60%\",\"不通过\",\"不通过\",\"COM42\",\"4.2\",\"1\"",
            "\"2026-11-10T09:44:24.000+08:00\",\"3\",\"右耳\",\"读取MAC地址\",\"65:61:96:2F:2B:2A\",\"通过\",\"不通过\",\"COM40\",\"3.8\",\"1\"",
            "\"2026-11-10T09:44:25.000+08:00\",\"3\",\"右耳\",\"获取电池电量\",\"99%\",\"通过\",\"不通过\",\"COM40\",\"3.8\",\"1\"");
        Directory.CreateDirectory(_path);
        string csvPath = Path.Combine(_path, "record.csv");
        File.WriteAllText(csvPath, csv, new UTF8Encoding(true));
        string xlsxPath = Path.Combine(_path, "record.xlsx");

        ProductionRecordExporter.Export(csvPath, xlsxPath, ["读取MAC地址", "获取电池电量"],
            new ProductionExportContext("X8I_V1", "DuCom V0.0.0.9"));

        string sheet = ReadSheet(xlsxPath);
        string stylesXml = ReadEntry(xlsxPath, "xl/styles.xml");
        Assert.Contains("产测记录 · X8I_V1", sheet);
        Assert.Contains("工具版本：DuCom V0.0.0.9", sheet);
        Assert.Contains("每次测试一行", sheet);
        Assert.Contains("测试总数 1　通过 0　不通过 1　通过率 0.0%", sheet);
        // Ports, friendly timestamps and round duration from the extended columns.
        Assert.Contains("COM42", sheet);
        Assert.Contains("COM40", sheet);
        Assert.Contains("2026-11-10 09:44:20", sheet);
        Assert.Contains("2026-11-10 09:44:25", sheet);
        Assert.Contains("4.9", sheet);
        // One pair test → exactly one data row.
        Assert.Contains("<row r=\"6\">", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<row r=\"7\">", sheet, StringComparison.Ordinal);
        // Failure styling on the failing item and verdicts, frozen header, widths.
        Assert.Contains("s=\"4\"", sheet);
        Assert.Contains("ySplit=\"5\"", sheet);
        Assert.Contains("<col min=\"1\"", sheet);
        Assert.Contains("FF9C0006", stylesXml);
    }

    [Fact]
    public void ConsecutiveOnceRunsEachGetTheirOwnRowViaTestSerial()
    {
        // "once" restarts the round counter every run: four executions all log
        // 轮次=1. Only the monotonic 测试号 keeps them as four separate records.
        string csv = string.Join("\r\n",
            "时间,轮次,通道,测试项,结果,状态,本轮结果,串口,耗时秒,测试号",
            "\"2026-11-10T10:13:47\",\"1\",\"左耳\",\"读取MAC地址\",\"64:65:48:F3:4D:05\",\"通过\",\"通过\",\"COM52\",\"1.0\",\"1\"",
            "\"2026-11-10T10:13:48\",\"1\",\"右耳\",\"读取MAC地址\",\"65:61:96:2F:2B:2A\",\"通过\",\"通过\",\"COM51\",\"1.0\",\"1\"",
            "\"2026-11-10T10:14:12\",\"1\",\"左耳\",\"读取MAC地址\",\"64:65:48:F3:4D:05\",\"通过\",\"通过\",\"COM52\",\"1.0\",\"2\"",
            "\"2026-11-10T10:14:13\",\"1\",\"右耳\",\"读取MAC地址\",\"65:61:96:2F:2B:2A\",\"通过\",\"通过\",\"COM51\",\"1.0\",\"2\"",
            "\"2026-11-10T10:14:40\",\"1\",\"左耳\",\"读取MAC地址\",\"64:65:48:F3:4D:05\",\"通过\",\"通过\",\"COM52\",\"1.0\",\"3\"",
            "\"2026-11-10T10:14:41\",\"1\",\"右耳\",\"读取MAC地址\",\"65:61:96:2F:2B:2A\",\"通过\",\"通过\",\"COM51\",\"1.0\",\"3\"",
            "\"2026-11-10T10:15:02\",\"1\",\"左耳\",\"读取MAC地址\",\"64:65:48:F3:4D:05\",\"通过\",\"不通过\",\"COM52\",\"1.0\",\"4\"",
            "\"2026-11-10T10:15:03\",\"1\",\"右耳\",\"读取MAC地址\",\"应答超时\",\"不通过\",\"不通过\",\"COM51\",\"1.0\",\"4\"");
        Directory.CreateDirectory(_path);
        string csvPath = Path.Combine(_path, "record.csv");
        File.WriteAllText(csvPath, csv, new UTF8Encoding(true));
        string xlsxPath = Path.Combine(_path, "record.xlsx");

        ProductionRecordExporter.Export(csvPath, xlsxPath, ["读取MAC地址"]);

        string sheet = ReadSheet(xlsxPath);
        Assert.Contains("测试号", sheet);
        Assert.Contains("测试总数 4　通过 3　不通过 1　通过率 75.0%", sheet);
        // Four once-runs → four data rows (r6..r9), not one merged row.
        Assert.Contains("<row r=\"6\">", sheet, StringComparison.Ordinal);
        Assert.Contains("<row r=\"9\">", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<row r=\"10\">", sheet, StringComparison.Ordinal);
        Assert.Contains("应答超时", sheet);
    }

    [Fact]
    public void MergedSessionFilesRenumberRunsGlobally()
    {
        // Two session files (the worker restarts between them, so both start at
        // 测试号=1). The merged export must keep them apart and number runs 1..3.
        Directory.CreateDirectory(_path);
        string csvA = Path.Combine(_path, "Production-X8I-20261110-100000-aaa.csv");
        string csvB = Path.Combine(_path, "Production-X8I-20261110-110000-bbb.csv");
        File.WriteAllText(csvA, string.Join("\r\n",
            "时间,轮次,通道,测试项,结果,状态,本轮结果,串口,耗时秒,测试号",
            "\"2026-11-10T10:00:01\",\"1\",\"左耳\",\"读取MAC地址\",\"11:22:33:44:55:66\",\"通过\",\"通过\",\"COM42\",\"1.0\",\"1\"",
            "\"2026-11-10T10:00:02\",\"1\",\"右耳\",\"读取MAC地址\",\"AA:BB:CC:DD:EE:FF\",\"通过\",\"通过\",\"COM40\",\"1.0\",\"1\"",
            "\"2026-11-10T10:05:01\",\"1\",\"左耳\",\"读取MAC地址\",\"11:22:33:44:55:66\",\"通过\",\"通过\",\"COM42\",\"1.0\",\"2\"",
            "\"2026-11-10T10:05:02\",\"1\",\"右耳\",\"读取MAC地址\",\"AA:BB:CC:DD:EE:FF\",\"通过\",\"通过\",\"COM40\",\"1.0\",\"2\""), new UTF8Encoding(true));
        File.WriteAllText(csvB, string.Join("\r\n",
            "时间,轮次,通道,测试项,结果,状态,本轮结果,串口,耗时秒,测试号",
            "\"2026-11-10T11:00:01\",\"1\",\"左耳\",\"读取MAC地址\",\"11:22:33:44:55:66\",\"通过\",\"通过\",\"COM42\",\"1.0\",\"1\"",
            "\"2026-11-10T11:00:02\",\"1\",\"右耳\",\"读取MAC地址\",\"应答超时\",\"不通过\",\"不通过\",\"COM40\",\"1.0\",\"1\""), new UTF8Encoding(true));

        string xlsxPath = Path.Combine(_path, "merged.xlsx");
        ProductionRecordExporter.Export([csvA, csvB], xlsxPath, ["读取MAC地址"]);

        string sheet = ReadSheet(xlsxPath);
        Assert.Contains("测试总数 3　通过 2　不通过 1　通过率 66.7%", sheet);
        // Three runs across two files → three data rows, numbered 1..3 globally.
        Assert.Contains("<row r=\"6\">", sheet, StringComparison.Ordinal);
        Assert.Contains("<row r=\"8\">", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<row r=\"9\">", sheet, StringComparison.Ordinal);
        Assert.Contains("应答超时", sheet);
    }

    [Fact]
    public void QuotedAndEmptyFieldsSurviveParsing()
    {
        List<string> fields = ProductionRecordExporter.ParseCsvLine("\"a,\"\"b\"\"\",,c");
        Assert.Equal(["a,\"b\"", "", "c"], fields);
    }

    [Fact]
    public void ColumnNamesFollowSpreadsheetLetters()
    {
        Assert.Equal("A", ProductionRecordExporter.ColumnName(0));
        Assert.Equal("Z", ProductionRecordExporter.ColumnName(25));
        Assert.Equal("AA", ProductionRecordExporter.ColumnName(26));
    }

    private static string ReadSheet(string xlsxPath) => ReadEntry(xlsxPath, "xl/worksheets/sheet1.xml");

    private static string ReadEntry(string xlsxPath, string name)
    {
        using FileStream stream = File.OpenRead(xlsxPath);
        using ZipArchive archive = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry entry = Assert.Single(archive.Entries.Where(e => e.FullName == name));
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_path)) Directory.Delete(_path, true); } catch { }
    }
}
