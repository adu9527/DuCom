using System.Net;
using System.Net.Sockets;
using System.Text;
using DuCom.Core.Diagnostics;
using DuCom.Core.Persistence;
using DuCom.Core.Telnet;
using Xunit;

namespace DuCom.Core.Tests.Telnet;

/// <summary>2026-08-28 review round 2 hardening tests across Telnet, worker, process, and visibility.</summary>
public sealed class TelnetHardeningTests
{
    private static int NextPort() => Random.Shared.Next(24_100, 24_900);

    [Fact]
    public async Task ServerSupportsRepeatedStartStopRestartAndFinalDispose()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        for (int round = 0; round < 3; round++)
        {
            server.Start(port);
            Assert.True(server.IsRunning);

            using (TcpClient client = new())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                using NetworkStream stream = client.GetStream();
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                byte[] buffer = new byte[256];
                int read = await stream.ReadAsync(buffer, timeout.Token);
                Assert.Contains("DuCom Telnet bridge ready.", Encoding.UTF8.GetString(buffer, 0, read));
            }

            // Two stops in a row must both be safe; the second finds nothing to do.
            await server.StopAsync();
            await server.StopAsync();
            Assert.False(server.IsRunning);
        }

        await server.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => server.Start(port));
    }

    [Fact]
    public void OversizedInputWithoutNewlineIsDroppedNotBufferedForever()
    {
        IncrementalUtf8LineFramer framer = new(maximumCommandLength: 64);

        // 1 MB without any newline in TCP-sized chunks: no line is emitted, the pending
        // buffer is capped, and the framer counts the overflow.
        byte[] chunk = new byte[4_096];
        Array.Fill(chunk, (byte)'x');
        int chunks = 256;
        for (int index = 0; index < chunks; index++)
        {
            Assert.Empty(framer.Append(chunk));
        }

        Assert.Equal(1, framer.OverflowCount);
        Assert.Null(framer.Flush()); // mid-discard of the oversized line

        // The terminator ends the oversized line; the NEXT line frames normally again.
        Assert.Empty(framer.Append("\r\n"u8.ToArray()));
        Assert.Equal(["ok"], framer.Append("ok\r\n"u8.ToArray()));
    }

    [Fact]
    public async Task CommandTaskRegisteredWithLifecycleSurvivesDisposeRace()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        using SemaphoreSlim sendStarted = new(0);
        using SemaphoreSlim allowSend = new(0);
        int sends = 0;
        TelnetSessionProbe probe = new(
            "COMX",
            _ => new DuCom.Core.Storage.LineStoreSnapshot(null, null, 0, []),
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref sends);
                sendStarted.Release();
                await allowSend.WaitAsync(cancellationToken);
            });

        await using TelnetBridgeCore core = new(server, _ => probe);
        server.Start(port);
        core.Bind("COMX");

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using NetworkStream stream = client.GetStream();
        _ = await stream.ReadAsync(new byte[256]);
        await stream.WriteAsync("blocked\r\n"u8.ToArray());

        using CancellationTokenSource seen = new(TimeSpan.FromSeconds(5));
        await sendStarted.WaitAsync(seen.Token);

        // Dispose while the command send is still in flight: disposal must cancel it and
        // complete without hanging and without losing the tracked task.
        using CancellationTokenSource disposeDeadline = new(TimeSpan.FromSeconds(5));
        await core.DisposeAsync().AsTask().WaitAsync(disposeDeadline.Token);

        allowSend.Release(); // unblock the (now cancelled) wait if needed
        Assert.Equal(1, Volatile.Read(ref sends));
    }

    [Fact]
    public async Task BindAfterDisposeThrows()
    {
        int port = NextPort();
        await using BasicTelnetServer server = new();
        await using TelnetBridgeCore core = new(server, _ => null);
        await core.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => core.Bind("COMX"));
    }
}

public sealed class PeriodicBackgroundWorkerHardeningTests
{
    [Fact]
    public async Task StartAfterDisposeThrowsObjectDisposedException()
    {
        await using PeriodicBackgroundWorker worker = new("test", TimeSpan.FromMilliseconds(50), _ => Task.CompletedTask);
        await worker.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => worker.Start());
    }

    [Fact]
    public async Task StartDisposeRaceNeverLeaksALoopTask()
    {
        // Start and Dispose issued concurrently from many tasks: every Start either
        // begins a loop that Dispose awaits, or throws ObjectDisposedException — there is
        // no interleaving that leaves an unawaited loop behind.
        List<Exception> unexpected = [];
        for (int round = 0; round < 20; round++)
        {
            PeriodicBackgroundWorker worker = new("race", TimeSpan.FromMilliseconds(10), _ => Task.CompletedTask);
            Task start = Task.Run(() =>
            {
                try
                {
                    worker.Start();
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception exception)
                {
                    lock (unexpected)
                    {
                        unexpected.Add(exception);
                    }
                }
            });
            ValueTask dispose = worker.DisposeAsync();
            await start;
            await dispose;
        }

        Assert.Empty(unexpected);
    }
}

public sealed class PortVisibilityTests
{
    [Fact]
    public void NormalizeTrimsDedupesCaseInsensitivelyAndKeepsOrder()
    {
        IReadOnlyList<string> normalized = DuCom.Core.Persistence.PortVisibility.NormalizeHidden(
            [" COM7 ", "com7", "", "  ", null, "COM3", "COM7"]);

        Assert.Equal(["COM7", "COM3"], normalized);
    }

    [Fact]
    public void MergeUnionsCurrentAndImported()
    {
        IReadOnlyList<string> merged = DuCom.Core.Persistence.PortVisibility.MergeHidden(
            ["COM3", "COM7"],
            ["com7", "COM9", ""]);

        Assert.Equal(["COM3", "COM7", "COM9"], merged);
    }

    [Fact]
    public void HiddenListSurvivesASettingsJsonRoundTrip()
    {
        // The settings snapshot persists this exact list shape; a round trip through
        // System.Text.Json plus the same normalizer on load must reproduce it (the
        // "hidden ports survive restart" contract at the persistence level).
        HiddenSnapshot captured = new(["COM7", "COM3"]);
        string json = System.Text.Json.JsonSerializer.Serialize(captured);

        HiddenSnapshot? restored = System.Text.Json.JsonSerializer.Deserialize<HiddenSnapshot>(json);
        Assert.NotNull(restored);
        Assert.Equal(
            DuCom.Core.Persistence.PortVisibility.NormalizeHidden(captured.HiddenPorts),
            DuCom.Core.Persistence.PortVisibility.NormalizeHidden(restored!.HiddenPorts));
    }

    private sealed record HiddenSnapshot(List<string>? HiddenPorts);
}

public sealed class PerSessionLogGateTests
{
    [Fact]
    public void AMismatchedSessionFailsOnlyItsOwnGate()
    {
        using TempDir directory = new();
        // LOAD1 files match its written bytes; LOAD2 files do not — the per-session gate
        // must flag exactly LOAD2 (two-session log mismatch, 2026-08-28 review).
        string load1 = Path.Combine(directory.Path, "LOAD1-0001.txt");
        string load2 = Path.Combine(directory.Path, "LOAD2-0001.txt");
        File.WriteAllText(load1, new string('a', 1_000), new UTF8Encoding(false));
        File.WriteAllText(load2, new string('b', 999), new UTF8Encoding(false));

        LoadCompletenessInfo session1 = EvaluateSession("LOAD1", directory.Path, writtenBytes: 1_000, inputBytes: 1_000);
        LoadCompletenessInfo session2 = EvaluateSession("LOAD2", directory.Path, writtenBytes: 1_000, inputBytes: 1_000);

        Assert.True(session1.IsComplete);
        Assert.False(session2.IsComplete);
        Assert.Contains("actual log file bytes 999 != written log bytes 1000", session2.FailureReason, StringComparison.Ordinal);
    }

    private static LoadCompletenessInfo EvaluateSession(string sessionName, string logDirectory, long writtenBytes, long inputBytes)
    {
        string[] files = Directory.GetFiles(logDirectory, $"{sessionName}-*.txt");
        long actualBytes = files.Sum(path => new FileInfo(path).Length);
        PipelineMetricsSnapshot metrics = new(
            10, inputBytes, 10, inputBytes, 10,
            10, writtenBytes, 10, 0, 2, 2, 0,
            ShutdownDrainState.Completed, TimeSpan.FromMilliseconds(1));
        SessionCloseGate gate = new(true, null, files.Length > 0, actualBytes);
        return LoadCompletenessEvaluator.Evaluate(metrics, 10, inputBytes, gate);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = Directory.CreateTempSubdirectory("ducom-session-gate-").FullName;

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
