using System.Diagnostics;
using DuCom.Core.Processes;
using Xunit;

namespace DuCom.Core.Tests.Processes;

/// <summary>
/// Real-helper-process tests for the bounded runner: large stdout, large stderr, hang,
/// nonzero exit, timeout with tree kill, and caller cancellation.
/// </summary>
public sealed class BoundedProcessTests
{
    private static string CmdPath => Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";

    private static string TempFileWithContent(string name, int size)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ducom-proc-{name}-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, new string('x', size));
        return path;
    }

    [Fact]
    public async Task LargeStdoutIsCapturedCompletely()
    {
        string big = TempFileWithContent("stdout", 1_000_000);
        try
        {
            BoundedProcessResult result = await BoundedProcess.RunAsync(
                CmdPath, $"/c type \"{big}\"", TimeSpan.FromSeconds(30));

            Assert.True(result.Succeeded);
            Assert.Equal(1_000_000, result.StandardOutput.Length);
            Assert.Equal(string.Empty, result.StandardError);
        }
        finally
        {
            File.Delete(big);
        }
    }

    [Fact]
    public async Task LargeStderrIsCapturedCompletely()
    {
        string big = TempFileWithContent("stderr", 1_000_000);
        try
        {
            BoundedProcessResult result = await BoundedProcess.RunAsync(
                CmdPath, $"/c type \"{big}\" 1>&2", TimeSpan.FromSeconds(30));

            Assert.True(result.Succeeded);
            Assert.Equal(1_000_000, result.StandardError.Length);
            Assert.Equal(string.Empty, result.StandardOutput);
        }
        finally
        {
            File.Delete(big);
        }
    }

    [Fact]
    public async Task LargeStdoutAndStderrTogetherDoNotDeadlock()
    {
        string outBig = TempFileWithContent("both-out", 700_000);
        string errBig = TempFileWithContent("both-err", 700_000);
        try
        {
            BoundedProcessResult result = await BoundedProcess.RunAsync(
                CmdPath, $"/c type \"{outBig}\" & type \"{errBig}\" 1>&2", TimeSpan.FromSeconds(30));

            Assert.True(result.Succeeded);
            Assert.Equal(700_000, result.StandardOutput.Length);
            Assert.Equal(700_000, result.StandardError.Length);
        }
        finally
        {
            File.Delete(outBig);
            File.Delete(errBig);
        }
    }

    [Fact]
    public async Task NonZeroExitCodeIsReported()
    {
        BoundedProcessResult result = await BoundedProcess.RunAsync(CmdPath, "/c exit 7", TimeSpan.FromSeconds(15));

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task HangingProcessIsKilledOnTimeout()
    {
        Stopwatch clock = Stopwatch.StartNew();
        BoundedProcessResult result = await BoundedProcess.RunAsync(
            CmdPath, "/c ping -n 60 -w 1000 127.0.0.1 > NUL", TimeSpan.FromSeconds(2));
        clock.Stop();

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"timeout took {clock.Elapsed}");
    }

    [Fact]
    public async Task CallerCancellationKillsTheProcess()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));
        BoundedProcessResult result = await BoundedProcess.RunAsync(
            CmdPath, "/c ping -n 60 -w 1000 127.0.0.1 > NUL", TimeSpan.FromSeconds(60), cancellationToken: cancellation.Token);

        Assert.False(result.TimedOut); // the caller cancelled, not the clock
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task TotalDeadlineBoundsKillAndDrainTime()
    {
        // A tiny timeout on a hanging child: the exit wait, the tree kill, and the final
        // stream drains together must stay within roughly the configured budget.
        TimeSpan timeout = TimeSpan.FromMilliseconds(300);
        Stopwatch clock = Stopwatch.StartNew();
        BoundedProcessResult result = await BoundedProcess.RunAsync(
            CmdPath, "/c ping -n 60 -w 1000 127.0.0.1 > NUL", timeout);
        clock.Stop();

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(
            clock.Elapsed < timeout + TimeSpan.FromSeconds(2),
            $"kill+drain blew the total deadline: {clock.Elapsed} for a {timeout} budget");
    }
}
