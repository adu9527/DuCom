using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuCom.Core.Diagnostics;

public static class LoadReportSerializer
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(LoadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateSchema(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string ToMarkdown(LoadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ValidateSchema(report);

        StringBuilder text = new();
        text.AppendLine("# DuCom Load Report");
        text.AppendLine();
        AppendLine(text, $"- Schema version: {report.SchemaVersion}");
        AppendLine(text, $"- Report ID: {report.ReportId}");
        AppendLine(text, $"- Created UTC: {report.CreatedAtUtc:O}");
        AppendLine(text, $"- Worktree: {report.WorktreeIdentifier}");
        AppendLine(text, $"- Machine: {report.Machine.MachineName}");
        AppendLine(text, $"- Runtime: {report.Machine.RuntimeVersion}");
        text.AppendLine();
        text.AppendLine("## Scenario");
        text.AppendLine();
        AppendLine(text, $"- Name: {report.Scenario.ScenarioName}");
        AppendLine(text, $"- Version: {report.Scenario.Version}");
        AppendLine(text, $"- Seed: {report.Scenario.Seed}");
        AppendLine(text, $"- Duration: {report.Scenario.Duration}");
        AppendLine(text, $"- Ports: {report.Scenario.PortCount}");
        AppendLine(text, $"- Target bytes/s/port: {report.Scenario.TargetBytesPerSecondPerPort}");
        AppendLine(text, $"- Payload profile: {report.Scenario.PayloadProfile}");
        AppendLine(text, $"- Chunk profile: {report.Scenario.ChunkProfile}");
        text.AppendLine();
        text.AppendLine("## Pipeline Metrics");
        text.AppendLine();
        text.AppendLine("| Metric | Value |");
        text.AppendLine("|---|---:|");
        AppendMetric(text, "Produced blocks", report.Pipeline.ProducedBlocks);
        AppendMetric(text, "Produced bytes", report.Pipeline.ProducedBytes);
        AppendMetric(text, "Accepted blocks", report.Pipeline.AcceptedBlocks);
        AppendMetric(text, "Accepted bytes", report.Pipeline.AcceptedBytes);
        AppendMetric(text, "Formatted log blocks", report.Pipeline.FormattedLogBlocks);
        AppendMetric(text, "Written log records", report.Pipeline.WrittenLogRecords);
        AppendMetric(text, "Written log bytes", report.Pipeline.WrittenLogBytes);
        AppendMetric(text, "Line records", report.Pipeline.LineRecords);
        AppendMetric(text, "Evictions", report.Pipeline.Evictions);
        AppendMetric(text, "Receive queue peak", report.Pipeline.ReceiveQueuePeak);
        AppendMetric(text, "Log queue peak", report.Pipeline.LogQueuePeak);
        AppendMetric(text, "Faults", report.Pipeline.Faults);
        AppendMetric(text, "Input acceptance", report.Pipeline.IsInputAcceptanceComplete ? "Complete" : "Incomplete");
        AppendMetric(text, "Log formatting coverage", report.Pipeline.IsLogFormattingCoverageComplete ? "Complete" : "Incomplete");
        AppendMetric(text, "Shutdown drain", report.Pipeline.ShutdownDrainState.ToString());
        AppendMetric(text, "Shutdown drain duration", report.Pipeline.ShutdownDrainDuration.ToString());

        if (report.Completeness is { } completeness)
        {
            text.AppendLine();
            text.AppendLine("## Generator Input & Completeness");
            text.AppendLine();
            AppendMetric(text, "Generator input blocks", completeness.GeneratorInputBlocks);
            AppendMetric(text, "Generator input bytes", completeness.GeneratorInputBytes);
            AppendMetric(text, "Pipeline produced blocks", completeness.PipelineProducedBlocks);
            AppendMetric(text, "Pipeline produced bytes", completeness.PipelineProducedBytes);
            AppendMetric(text, "Written log records", completeness.WrittenLogRecords);
            AppendMetric(text, "Written log bytes", completeness.WrittenLogBytes);
            AppendMetric(text, "Actual log file bytes", completeness.ActualLogBytes);
            AppendMetric(text, "Log files exist", completeness.LogFilesExist ? "Yes" : "No");
            AppendMetric(text, "Session close", completeness.CloseSucceeded ? "Succeeded" : "Failed");
            AppendMetric(text, "Session fault", completeness.SessionFaultFree ? "None" : "Present");
            AppendMetric(text, "Completeness", completeness.IsComplete ? "Complete" : $"INCOMPLETE: {completeness.FailureReason}");
        }
        text.AppendLine();
        text.AppendLine("## Process Metrics");
        text.AppendLine();
        AppendLine(text, $"- Elapsed: {report.Process.Elapsed}");
        AppendLine(text, $"- Throughput bytes/s: {Format(report.Process.ThroughputBytesPerSecond)}");
        AppendLine(text, $"- Allocation bytes/s: {Format(report.Process.AllocationBytesPerSecond)}");
        AppendLine(text, $"- GC collections: {report.Process.Gen0Collections}/{report.Process.Gen1Collections}/{report.Process.Gen2Collections}");
        AppendLine(text, $"- Working set bytes start/peak/end: {report.Process.WorkingSetStartBytes}/{report.Process.WorkingSetPeakBytes}/{report.Process.WorkingSetEndBytes}");
        AppendLine(text, $"- Private memory bytes start/peak/end: {report.Process.PrivateMemoryStartBytes}/{report.Process.PrivateMemoryPeakBytes}/{report.Process.PrivateMemoryEndBytes}");
        AppendLine(text, $"- CPU time: {report.Process.CpuTime}");
        AppendLine(text, $"- Thread count: {report.Process.ThreadCount}");

        if (!string.IsNullOrWhiteSpace(report.Notes))
        {
            text.AppendLine();
            text.AppendLine("## Notes");
            text.AppendLine();
            text.AppendLine(report.Notes);
        }

        return text.ToString();
    }

    private static void AppendLine(StringBuilder text, FormattableString value) =>
        text.AppendLine(value.ToString(CultureInfo.InvariantCulture));

    private static void AppendMetric(StringBuilder text, string name, object value) =>
        AppendLine(text, $"| {name} | {value} |");

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void ValidateSchema(LoadReport report)
    {
        if (report.SchemaVersion != LoadReport.CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(report),
                report.SchemaVersion,
                $"Unsupported load report schema version. Expected {LoadReport.CurrentSchemaVersion}.");
        }
    }
}
