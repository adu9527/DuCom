using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DuCom.Core.Diagnostics;
using DuCom.Core.Ports;
using DuCom.LoadGenerator;

Dictionary<string, string> arguments = ParseArguments(args);
StandardLoadScenario standardScenario = StandardLoadScenarios.Get(Get(arguments, "scenario", "dual-1m-mixed"));
LoadGeneratorOptions defaults = standardScenario.GeneratorOptions;
LoadGeneratorOptions options = new(
    GetInt(arguments, "seed", defaults.Seed),
    TimeSpan.FromSeconds(GetDouble(arguments, "duration-seconds", defaults.Duration.TotalSeconds)),
    GetLong(arguments, "bytes-per-second", defaults.TargetBytesPerSecondPerPort),
    GetInt(arguments, "min-chunk", defaults.MinimumChunkSize),
    GetInt(arguments, "max-chunk", defaults.MaximumChunkSize),
    GetInt(arguments, "ports", defaults.PortCount),
    Enum.Parse<LoadPayloadProfile>(Get(arguments, "profile", defaults.PayloadProfile.ToString()), true));
bool pace = GetBool(arguments, "pace", standardScenario.PaceByScheduledOffset);
string outputDirectory = Path.GetFullPath(Get(arguments, "output", Path.Combine("reports", "generated")));
string targetName = Get(arguments, "target", "generator");

using Process process = Process.GetCurrentProcess();
long allocatedBefore = GC.GetTotalAllocatedBytes(true);
int gen0Before = GC.CollectionCount(0);
int gen1Before = GC.CollectionCount(1);
int gen2Before = GC.CollectionCount(2);
process.Refresh();
long workingSetStart = process.WorkingSet64;
long privateMemoryStart = process.PrivateMemorySize64;
TimeSpan cpuStart = process.TotalProcessorTime;

PipelineMetricsSnapshot metrics;
TimeSpan elapsed;
string? faultMessage;
LoadCompletenessInfo? completeness;
List<string> extraNotes = [];
if (string.Equals(targetName, "serial-session", StringComparison.OrdinalIgnoreCase))
{
    SerialSessionLoadResult sessionResult = await SerialSessionLoadRunner.RunAsync(
        options,
        Path.Combine(outputDirectory, "session-logs"),
        pace);
    metrics = sessionResult.Metrics;
    elapsed = sessionResult.Elapsed;
    faultMessage = sessionResult.FaultMessage;

    // Per-session gate (2026-08-28 review): every session is verified on its own —
    // generator input vs produced/accepted/formatted, close result, fault, its own log
    // files, and its actual on-disk bytes against its own WrittenLogBytes.
    List<string> sessionFailures = [];
    foreach (SessionLoadGate session in sessionResult.PerSession)
    {
        SessionCloseGate gate = new(
            session.CloseResult is PortCommandResult.Succeeded or PortCommandResult.AlreadyClosed,
            session.FaultMessage,
            session.LogFilesExist,
            session.ActualLogBytes);
        LoadCompletenessInfo sessionInfo = LoadCompletenessEvaluator.Evaluate(
            session.Metrics,
            session.GeneratorInputBlocks,
            session.GeneratorInputBytes,
            gate);
        extraNotes.Add($"session {session.PortName}: {(sessionInfo.IsComplete ? "complete" : $"INCOMPLETE: {sessionInfo.FailureReason}")}");
        if (!sessionInfo.IsComplete)
        {
            sessionFailures.Add($"{session.PortName}: {sessionInfo.FailureReason}");
        }
    }

    // File bytes are measured after disposal so every writer has flushed and closed.
    SessionCloseGate closeGate = new(
        sessionResult.AllSessionsClosedCleanly,
        sessionResult.FaultMessage,
        sessionResult.PerSession.All(session => session.LogFilesExist),
        sessionResult.PerSession.Sum(session => session.ActualLogBytes));
    LoadCompletenessInfo aggregate = LoadCompletenessEvaluator.Evaluate(metrics, sessionResult.GeneratorInputBlocks, sessionResult.GeneratorInputBytes, closeGate);
    List<string> combinedFailures = [];
    if (aggregate.FailureReason.Length > 0)
    {
        combinedFailures.Add(aggregate.FailureReason);
    }

    combinedFailures.AddRange(sessionFailures);
    completeness = aggregate with
    {
        IsComplete = aggregate.IsComplete && sessionFailures.Count == 0,
        FailureReason = combinedFailures.Count == 0 ? aggregate.FailureReason : string.Join("; ", combinedFailures),
    };
}
else
{
    ILoadBlockTarget target = CreateTarget(standardScenario);
    LoadRunResult result = await InMemoryLoadRunner.RunAsync(options, target, pace);
    metrics = result.Metrics;
    elapsed = result.Elapsed;
    faultMessage = result.FaultMessage;
    completeness = LoadCompletenessEvaluator.Evaluate(
        metrics,
        result.Metrics.ProducedBlocks,
        result.Metrics.ProducedBytes);
}

long allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
process.Refresh();
long workingSetEnd = process.WorkingSet64;
long privateMemoryEnd = process.PrivateMemorySize64;
TimeSpan cpuTime = process.TotalProcessorTime - cpuStart;

LoadReport report = new(
    LoadReport.CurrentSchemaVersion,
    $"m0-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
    DateTimeOffset.UtcNow,
    "uncommitted-worktree",
    new MachineInfo(Environment.MachineName, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, Environment.ProcessorCount),
    new LoadScenarioInfo(
        standardScenario.Name,
        standardScenario.Version,
        options.Seed,
        options.Duration,
        options.PortCount,
        options.TargetBytesPerSecondPerPort,
        options.PayloadProfile.ToString(),
        $"uniform-{options.MinimumChunkSize}-{options.MaximumChunkSize}"),
    metrics,
    new ProcessMetricsSnapshot(
        elapsed,
        metrics.AcceptedBytes / Math.Max(elapsed.TotalSeconds, double.Epsilon),
        allocatedBytes / Math.Max(elapsed.TotalSeconds, double.Epsilon),
        GC.CollectionCount(0) - gen0Before,
        GC.CollectionCount(1) - gen1Before,
        GC.CollectionCount(2) - gen2Before,
        workingSetStart,
        Math.Max(workingSetStart, workingSetEnd),
        workingSetEnd,
        privateMemoryStart,
        Math.Max(privateMemoryStart, privateMemoryEnd),
        privateMemoryEnd,
        cpuTime,
        process.Threads.Count),
    CreateNotes(standardScenario, pace, targetName, faultMessage, extraNotes),
    completeness);

Directory.CreateDirectory(outputDirectory);
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "load-report.json"), LoadReportSerializer.ToJson(report));
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "load-report.md"), LoadReportSerializer.ToMarkdown(report));
Console.WriteLine($"Scenario: {standardScenario.Name} v{standardScenario.Version}");
Console.WriteLine($"Generator input: {completeness?.GeneratorInputBlocks ?? 0} blocks/{completeness?.GeneratorInputBytes ?? 0} bytes; pipeline produced {metrics.ProducedBlocks} blocks/{metrics.ProducedBytes} bytes.");
Console.WriteLine($"Produced {metrics.ProducedBlocks} blocks/{metrics.ProducedBytes} bytes; accepted {metrics.AcceptedBlocks} blocks/{metrics.AcceptedBytes} bytes.");
Console.WriteLine($"Formatted {metrics.FormattedLogBlocks} blocks; wrote {metrics.WrittenLogRecords} records/{metrics.WrittenLogBytes} bytes; actual log files {completeness?.ActualLogBytes ?? 0} bytes.");
foreach (string note in extraNotes)
{
    Console.WriteLine(note);
}
Console.WriteLine($"Drain: {metrics.ShutdownDrainState}; faults: {metrics.Faults}; elapsed: {elapsed}.");
Console.WriteLine("Completeness: " + (completeness is null ? "Unknown" : completeness.IsComplete ? "Complete" : $"INCOMPLETE: {completeness.FailureReason}"));
Console.WriteLine($"Reports: {outputDirectory}");

return metrics.ShutdownDrainState is ShutdownDrainState.Completed && (completeness?.IsComplete ?? true) ? 0 : 1;

static ILoadBlockTarget CreateTarget(StandardLoadScenario scenario) => scenario.TargetBehavior switch
{
    LoadTargetBehavior.Immediate => new ImmediateLoadBlockTarget(),
    LoadTargetBehavior.Slow => new DelayedLoadBlockTarget(scenario.TargetDelay),
    LoadTargetBehavior.Failing => new FailingLoadBlockTarget(
        scenario.FailAfterAcceptedBlocks
        ?? throw new InvalidOperationException("Failing target scenario requires FailAfterAcceptedBlocks.")),
    _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario.TargetBehavior, null),
};

static string CreateNotes(StandardLoadScenario scenario, bool pace, string targetName, string? faultMessage, IReadOnlyList<string> extraNotes)
{
    string notes = $"In-memory harness; target={targetName}; behavior={scenario.TargetBehavior}; paced={pace}.";
    if (faultMessage is not null)
    {
        notes += $"{Environment.NewLine}{faultMessage}";
    }

    foreach (string note in extraNotes)
    {
        notes += $"{Environment.NewLine}{note}";
    }

    return notes;
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
    for (int index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= values.Length)
        {
            throw new ArgumentException("Arguments must use --name value pairs.");
        }

        result[values[index][2..]] = values[index + 1];
    }

    return result;
}

static string Get(IReadOnlyDictionary<string, string> values, string name, string defaultValue) =>
    values.TryGetValue(name, out string? value) ? value : defaultValue;

static int GetInt(IReadOnlyDictionary<string, string> values, string name, int defaultValue) =>
    int.Parse(Get(values, name, defaultValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

static long GetLong(IReadOnlyDictionary<string, string> values, string name, long defaultValue) =>
    long.Parse(Get(values, name, defaultValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

static double GetDouble(IReadOnlyDictionary<string, string> values, string name, double defaultValue) =>
    double.Parse(Get(values, name, defaultValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

static bool GetBool(IReadOnlyDictionary<string, string> values, string name, bool defaultValue) =>
    bool.Parse(Get(values, name, defaultValue.ToString(CultureInfo.InvariantCulture)));
