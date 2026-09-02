using System.Diagnostics;
using System.IO;
using DuCom.Core.Processes;

namespace DuCom.Services;

public sealed record Com0ComCommandResult(bool Succeeded, string Output, int ExitCode)
{
    public static Com0ComCommandResult Failed(string message) => new(false, message, -1);
}

/// <summary>
/// App-layer process service around the com0com setupc.exe tool. Only whitelisted command
/// verbs are accepted; stdout/stderr are captured concurrently with the bounded runner
/// (timeout clock starts at process start, timeouts kill the process tree); nothing runs
/// elevated implicitly — when setupc requires elevation the failure output is surfaced so
/// the user can restart DuCom as administrator.
/// </summary>
public static class Com0ComService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>setupc command verbs DuCom is allowed to run.</summary>
    public static readonly IReadOnlyList<string> AllowedVerbs = ["list", "install", "remove", "change"];

    public static async Task<Com0ComCommandResult> RunAsync(string setupcPath, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setupcPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);
        if (!IsAllowedArguments(arguments))
        {
            return Com0ComCommandResult.Failed($"Command rejected (verb not allowed): {arguments}");
        }

        if (!File.Exists(setupcPath))
        {
            return Com0ComCommandResult.Failed($"setupc.exe not found: {setupcPath}");
        }

        try
        {
            BoundedProcessResult result = await BoundedProcess.RunAsync(
                setupcPath,
                arguments,
                CommandTimeout,
                workingDirectory: Path.GetDirectoryName(setupcPath) ?? Environment.CurrentDirectory).ConfigureAwait(false);
            if (result.TimedOut)
            {
                return Com0ComCommandResult.Failed(
                    $"setupc timed out after {CommandTimeout.TotalSeconds:0}s and was force-closed: {arguments}");
            }

            string combined = result.CombinedOutput;
            bool succeeded = result.Succeeded && !combined.Contains("administrator", StringComparison.OrdinalIgnoreCase);
            return new Com0ComCommandResult(succeeded, combined, result.ExitCode);
        }
        catch (Exception exception)
        {
            return Com0ComCommandResult.Failed($"setupc failed to start: {exception.Message}");
        }
    }

    /// <summary>True when the first token of the argument string is a whitelisted verb.</summary>
    public static bool IsAllowedArguments(string arguments)
        => Com0ComParser.IsAllowedArguments(arguments, AllowedVerbs);

    /// <summary>Parses `setupc list` output lines like "CNCA0 PortName=COM5,EmuBR=yes".</summary>
    public static IReadOnlyList<Com0ComPortEntry> ParseList(string output)
        => Com0ComParser.ParseList(output);

    public static IReadOnlyList<Com0ComPortPair> PairEntries(IReadOnlyList<Com0ComPortEntry> entries)
        => Com0ComParser.PairEntries(entries);

    public static bool IsValidPortName(string? name)
        => Com0ComParser.IsValidPortName(name);
}
