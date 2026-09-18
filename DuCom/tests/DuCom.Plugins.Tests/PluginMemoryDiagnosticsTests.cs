using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using Xunit;

namespace DuCom.Plugins.Tests;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "xUnit IAsyncLifetime disposes the service.")]
public sealed class PluginMemoryDiagnosticsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ducom-memory-diagnostics-{Guid.NewGuid():N}");
    private PluginSystemService _service = null!;
    private static readonly MethodInfo LogSample = typeof(PluginSystemService)
        .GetMethod("LogMemorySample", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static PluginBudgetSample Sample => new()
    {
        HostPrivateBytes = 1234567,
        TotalPrivateBytes = 1234597,
        ManagedHeapBytes = 234567,
        ManagedHeapSizeBytes = 345678,
        ManagedFragmentedBytes = 45678,
        ProcessThreadCount = 1,
        ThreadPoolThreadCount = 1,
        ThreadPoolPendingWorkItemCount = 0,
        Workers = [("org.example.one", 42, 10), ("org.example.two", 43, 20)],
    };

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "unused-host.exe"), "Test fixture; never launched.");
        _service = new PluginSystemService(new PluginHostPaths(_root), new FakeEnvironment(),
            Path.Combine(_root, "unused-host.exe"));
        return Task.CompletedTask;
    }

    private void Emit(long timestamp, PluginBudgetSample? sample = null) =>
        LogSample.Invoke(_service, [sample ?? Sample, timestamp]);

    [Fact]
    public void FirstAndExactSixtySecondBoundaryLogWithoutCatchUpBursts()
    {
        List<string> logs = [];
        _service.ProgramLog += logs.Add;
        Emit(1);
        Emit(1);
        Emit(60L * Stopwatch.Frequency);
        Assert.Single(logs);
        Emit(1 + 60L * Stopwatch.Frequency);
        Assert.Equal(2, logs.Count);
        Emit(1 + 600L * Stopwatch.Frequency);
        Emit(1 + 600L * Stopwatch.Frequency);
        Assert.Equal(3, logs.Count);
    }

    [Fact]
    public void ConcurrentSamplesReserveOnlyOneLogPerInterval()
    {
        ConcurrentQueue<string> logs = new();
        _service.ProgramLog += logs.Enqueue;
        Parallel.For(0, 100, _ => Emit(1));
        Assert.Single(logs);
        Parallel.For(0, 100, _ => Emit(1 + 60L * Stopwatch.Frequency));
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void NoSubscriberDoesNotConsumeFirstSampleAndReentrancyIsThrottled()
    {
        Emit(1);
        int count = 0;
        _service.ProgramLog += _ => { count++; Emit(1); };
        Emit(1);
        Assert.Equal(1, count);
    }

    [Fact]
    public void LogsInvariantBytesAndAllSampledWorkers()
    {
        List<string> logs = [];
        _service.ProgramLog += logs.Add;
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Emit(1);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        string log = Assert.Single(logs);
        Assert.StartsWith($"Memory sample: unit=bytes hostPid={Environment.ProcessId} hostPrivate=1234567 ", log);
        string workingSet = log.Split("hostWorkingSet=")[1].Split(' ')[0];
        Assert.True(long.Parse(workingSet, CultureInfo.InvariantCulture) > 0);
        Assert.Contains("totalPrivate=1234597 managed=234567 heap=345678 fragmented=45678", log);
        Assert.Contains("workers=[pid=42 private=10 plugin=\"org.example.one\"; pid=43 private=20 plugin=\"org.example.two\"]", log);
    }

    [Fact]
    public void EmptyWorkersAndPluginIdsRemainSingleLine()
    {
        List<string> logs = [];
        _service.ProgramLog += logs.Add;
        Emit(1, Sample with { Workers = [] });
        Assert.EndsWith("workers=[]", Assert.Single(logs));
        Emit(1 + 60L * Stopwatch.Frequency, Sample with { Workers = [("bad\r\nname", 42, 10)] });
        Assert.DoesNotContain("\r", logs[1]);
        Assert.DoesNotContain("\n", logs[1]);
    }

    [Fact]
    public void SinkFailureIsThrottledAndDoesNotInterruptBudgetSampleEvent()
    {
        int attempts = 0;
        int samples = 0;
        _service.ProgramLog += _ => { attempts++; throw new IOException("sink unavailable"); };
        _service.Budget.Sampled += _ => samples++;
        _service.Budget.Sample();
        _service.Budget.Sample();
        Assert.Equal(1, attempts);
        Assert.Equal(2, samples);
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
