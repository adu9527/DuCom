using System.Text.Json;
using DuCom.Core.Diagnostics;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class LoadReportTests
{
    [Fact]
    public void JsonReportUsesVersionedCamelCaseSchemaAndRoundTrips()
    {
        LoadReport report = CreateReport();

        string json = LoadReportSerializer.ToJson(report);
        LoadReport? restored = JsonSerializer.Deserialize<LoadReport>(json, LoadReportSerializer.JsonOptions);

        Assert.Contains("\"schemaVersion\": 3", json);
        Assert.Contains("\"scenarioName\": \"dual-1m-mixed\"", json);
        Assert.Equal(report, restored);
    }

    [Fact]
    public void MarkdownReportContainsIdentityScenarioMetricsAndCompleteness()
    {
        string markdown = LoadReportSerializer.ToMarkdown(CreateReport());

        Assert.Contains("# DuCom Load Report", markdown);
        Assert.Contains("dual-1m-mixed", markdown);
        Assert.Contains("worktree-123", markdown);
        Assert.Contains("Produced blocks | 10", markdown);
        Assert.Contains("Input acceptance | Complete", markdown);
        Assert.Contains("Log formatting coverage | Complete", markdown);
        Assert.Contains("Shutdown drain | Completed", markdown);
    }

    private static LoadReport CreateReport() => new(
        SchemaVersion: LoadReport.CurrentSchemaVersion,
        ReportId: "report-001",
        CreatedAtUtc: new DateTimeOffset(2026, 8, 26, 8, 30, 0, TimeSpan.Zero),
        WorktreeIdentifier: "worktree-123",
        Machine: new MachineInfo("machine", "Windows", ".NET 10", 8),
        Scenario: new LoadScenarioInfo(
            "dual-1m-mixed",
            1,
            12345,
            TimeSpan.FromSeconds(10),
            2,
            100_000,
            "mixed-lines",
            "uniform-64-512"),
        Pipeline: new PipelineMetricsSnapshot(
            10,
            1_000,
            10,
            1_000,
            10,
            10,
            1_200,
            20,
            0,
            3,
            4,
            0,
            ShutdownDrainState.Completed,
            TimeSpan.FromMilliseconds(5)),
        Process: new ProcessMetricsSnapshot(
            TimeSpan.FromSeconds(10),
            100,
            10,
            0,
            0,
            0,
            100,
            120,
            110,
            80,
            90,
            85,
            TimeSpan.FromSeconds(2),
            12),
        Notes: "deterministic test");
}
