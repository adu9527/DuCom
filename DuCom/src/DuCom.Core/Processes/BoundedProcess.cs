using System.ComponentModel;
using System.Diagnostics;

namespace DuCom.Core.Processes;

public sealed record BoundedProcessResult(
    bool Succeeded,
    bool TimedOut,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public string CombinedOutput => (StandardOutput + StandardError).Trim();
}

/// <summary>
/// Runs one external process with concurrent stdout/stderr capture and a wall-clock
/// timeout that starts at process start and bounds the WHOLE operation — waiting for
/// exit, the tree kill, and the final stdout/stderr drains share one total deadline, so
/// the call always returns within roughly the configured timeout. On timeout or
/// cancellation the entire process tree is killed and the result carries whatever output
/// was captured before the deadline. No fixed sleeps are used anywhere.
/// </summary>
public static class BoundedProcess
{
    /// <summary>
    /// Small slack added to the configured timeout so a cooperative process finishing
    /// exactly at the deadline is not misreported; the hard total bound stays proportional
    /// to the caller's budget.
    /// </summary>
    private static readonly TimeSpan DeadlineSlack = TimeSpan.FromMilliseconds(250);

    public static async Task<BoundedProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        return await RunAsync(startInfo, timeout, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BoundedProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            return new BoundedProcessResult(false, false, -1, string.Empty, "The process did not start (already exited immediately).");
        }

        // Total deadline: process start + timeout (+ small slack). Every wait below —
        // exit wait, kill, and both stream drains — is bounded by the REMAINING time on
        // this single clock, so the whole call is bounded by roughly `timeout`.
        long deadlineTicks = Stopwatch.GetTimestamp() + (long)((timeout + DeadlineSlack).TotalSeconds * Stopwatch.Frequency);

        // Timeout clock starts here — at process start, not after the streams finish. The
        // kill registration fires the moment the bounded token trips, closing the pipes.
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        using CancellationTokenRegistration killRegistration = bounded.Token.Register(
            () => KillProcessTree(process),
            useSynchronizationContext: false);

        // The drains carry no token on purpose: on timeout the tree kill closes the pipes,
        // so everything the child wrote before dying is still returned — but only until
        // the total deadline expires.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            bool timedOut = !cancellationToken.IsCancellationRequested;
            string output = await CaptureDrainAsync(stdout, deadlineTicks).ConfigureAwait(false);
            string error = await CaptureDrainAsync(stderr, deadlineTicks).ConfigureAwait(false);
            return new BoundedProcessResult(false, timedOut, -1, output, error);
        }

        string normalOutput = await CaptureDrainAsync(stdout, deadlineTicks).ConfigureAwait(false);
        string normalError = await CaptureDrainAsync(stderr, deadlineTicks).ConfigureAwait(false);
        bool deadlineBlown = Stopwatch.GetTimestamp() >= deadlineTicks;
        return new BoundedProcessResult(
            process.ExitCode == 0 && !deadlineBlown,
            deadlineBlown,
            deadlineBlown ? -1 : process.ExitCode,
            normalOutput,
            deadlineBlown ? AppendDeadlineNote(normalError) : normalError);
    }

    private static string AppendDeadlineNote(string error) =>
        string.IsNullOrEmpty(error)
            ? "The total process deadline (exit wait + output drains) was exceeded; captured output may be truncated."
            : error + "\r\nThe total process deadline (exit wait + output drains) was exceeded; captured output may be truncated.";

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between cancel and kill.
        }
        catch (Win32Exception)
        {
            // Access denied or already gone; the drains are still deadline-bounded.
        }
    }

    /// <summary>Drains one output stream, bounded by whatever remains on the total deadline.</summary>
    private static async Task<string> CaptureDrainAsync(Task<string> drain, long deadlineTicks)
    {
        long remainingTicks = deadlineTicks - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return string.Empty;
        }

        try
        {
            TimeSpan remaining = TimeSpan.FromMilliseconds(remainingTicks * 1000.0 / Stopwatch.Frequency);
            return await drain.WaitAsync(remaining).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
