using System.Text;
using DuCom.Core.LogAnalysis;

namespace DuCom.Core.Tests.LogAnalysis;

public sealed class LogAnalyzerTests
{
    [Fact]
    public void BuiltInRulePackContainsAllSearchTextEntries()
    {
        IReadOnlyList<LogAnalyzerRule> rules = LogAnalyzerRuleService.LoadDefaults();

        Assert.Equal(71, rules.Count);
        Assert.Contains(rules, rule => rule.Pattern == "ASSERT" && rule.Category == "异常");
        Assert.Contains(rules, rule => rule.Pattern == "04 03 0b" && rule.Name == "Connection Complete event");
        Assert.All(rules, rule => Assert.True(rule.IsEnabled));
    }

    [Fact]
    public void AnalyseDocImportPreservesLiteralTextAndColors()
    {
        const string xml = "<AnalyseDoc><SearchText doSearch=\"false\" color=\"blue\" bgColor=\"yellow\" comment=\"Notify\">[Notify]</SearchText></AnalyseDoc>";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(xml));

        LogAnalyzerRule rule = Assert.Single(AnalyseDocRuleImporter.Import(stream));

        Assert.Equal("[Notify]", rule.Pattern);
        Assert.Equal("Notify", rule.Name);
        Assert.Equal((byte)37, rule.ForegroundR);
        Assert.Equal((byte)250, rule.BackgroundR);
        Assert.True(rule.IsEnabled);
    }

    [Fact]
    public void ParserExtractsBesFieldsAndKeywordMatches()
    {
        LogAnalyzerRule rule = new(Guid.NewGuid(), "assert", "断言异常", "异常", "ASSERT", false, true, null, null, null, null, null, null);
        LogAnalyzerParser parser = new([rule]);
        DateTimeOffset received = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

        LogAnalyzerRecord record = parser.Parse(1, "s1", "COM7", "L", received, TimeSpan.FromMilliseconds(10),
            "[12:34:56.789]       741/E/NONE  /  1 | ASSERT failed");

        Assert.Equal("ERROR", record.Level);
        Assert.Equal("NONE", record.Module);
        Assert.Equal("ASSERT failed", record.Message);
        Assert.Equal("断言异常", record.ChineseComment);
        Assert.Equal(new TimeSpan(12, 34, 56) + TimeSpan.FromMilliseconds(789), record.BaseDisplayTime.TimeOfDay);
        Assert.Equal(new TimeSpan(12, 34, 56) + TimeSpan.FromMilliseconds(799), record.DisplayTime.TimeOfDay);
        Assert.Single(record.MatchedRules);
    }

    [Fact]
    public void UnmatchedStructuredLineStillHasChineseComment()
    {
        LogAnalyzerParser parser = new([]);

        LogAnalyzerRecord record = parser.Parse(1, "s1", "COM7", "L", DateTimeOffset.Now, TimeSpan.Zero,
            "741/I/BT_APP  /  1 | normal trace");

        Assert.Equal("BT_APP 模块运行日志", record.ChineseComment);
    }

    [Fact]
    public void SearchIncludesParsedAndChineseFields()
    {
        LogAnalyzerRule rule = new(Guid.NewGuid(), "connect", "连接完成事件", "连接", "CONNECTED", false, true,
            null, null, null, null, null, null);
        LogAnalyzerRecord record = new(1, "s1", "COM7", "L", DateTimeOffset.Now, DateTimeOffset.Now,
            DateTimeOffset.Now, "INFO", "BT_APP", "CONNECTED", "CONNECTED", [rule]);

        Assert.True(LogAnalyzerOperations.MatchesSearch(record, "连接完成"));
        Assert.True(LogAnalyzerOperations.MatchesSearch(record, "BT_APP"));
        Assert.True(LogAnalyzerOperations.MatchesSearch(record, "COM7"));
        Assert.False(LogAnalyzerOperations.MatchesSearch(record, "not-present"));
    }

    [Fact]
    public void FileLoaderCarriesDateAcrossMidnightAndReportsTruncation()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(path,
            [
                "[23:59:59.900] 1/I/MOD / 1 | before",
                "[00:00:00.100] 2/I/MOD / 1 | after",
                "[00:00:00.200] 3/I/MOD / 1 | last",
            ]);
            LogAnalyzerFileLoadResult result = LogAnalyzerFileLoader.Load(
                [new LogAnalyzerFileSource(path, "L")], new LogAnalyzerParser([]), 2, Sequence);

            Assert.Equal(3, result.TotalLineCount);
            Assert.Equal(1, result.TruncatedLineCount);
            Assert.Equal(2, result.Records.Count);
            Assert.True(result.Records[1].DisplayTime > result.Records[0].DisplayTime);
        }
        finally
        {
            File.Delete(path);
        }

        long Sequence() => 1;
    }
}
