using System.Diagnostics;
using System.IO;
using System.Text;

namespace DuCom.Core.Updates;

/// <summary>
/// Downloads a new portable executable next to the running one and stages an
/// apply script that waits for the current process to exit before swapping files.
/// Paths are handed to the script through environment variables so the script
/// itself stays pure ASCII regardless of the installation path.
/// </summary>
public sealed class PortableUpdateStager
{
    private const string TargetVariable = "DUCOM_UPDATE_TARGET";
    private const string StagedVariable = "DUCOM_UPDATE_STAGED";
    private const string ProcessIdVariable = "DUCOM_UPDATE_PID";

    private readonly string _targetExecutablePath;

    public string StagingDirectory { get; }

    public string StagedFilePath { get; }

    private string PartFilePath => StagedFilePath + ".part";

    private string ScriptFilePath => Path.Combine(StagingDirectory, "apply-update.cmd");

    public PortableUpdateStager(string? targetExecutablePath = null)
    {
        _targetExecutablePath = targetExecutablePath
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The portable updater requires a running executable path.");
        string? directory = Path.GetDirectoryName(_targetExecutablePath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException($"Could not resolve the directory of '{_targetExecutablePath}'.");
        }

        StagingDirectory = Path.Combine(directory, "Updates");
        StagedFilePath = Path.Combine(StagingDirectory, $"{Path.GetFileNameWithoutExtension(_targetExecutablePath)}.exe.new");
    }

    public bool HasStagedPackage => File.Exists(StagedFilePath);

    public async Task StagePackageAsync(
        GitHubReleaseClient client,
        GitHubReleaseAsset asset,
        IProgress<long>? bytesProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(asset);

        Directory.CreateDirectory(StagingDirectory);
        await client.DownloadAssetAsync(asset, PartFilePath, bytesProgress, cancellationToken).ConfigureAwait(false);
        if (File.Exists(StagedFilePath))
        {
            File.Delete(StagedFilePath);
        }

        File.Move(PartFilePath, StagedFilePath);
    }

    /// <summary>Launches the apply script and returns; the caller is responsible for exiting the process.</summary>
    public void ApplyStagedUpdate()
    {
        if (!HasStagedPackage)
        {
            throw new InvalidOperationException("No staged update package was found.");
        }

        File.WriteAllText(ScriptFilePath, BuildApplyScript(), Encoding.ASCII);
        ProcessStartInfo startInfo = new("cmd.exe", $"/c \"{ScriptFilePath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = StagingDirectory,
        };
        startInfo.EnvironmentVariables[TargetVariable] = _targetExecutablePath;
        startInfo.EnvironmentVariables[StagedVariable] = StagedFilePath;
        startInfo.EnvironmentVariables[ProcessIdVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Process.Start(startInfo);
    }

    internal static string BuildApplyScript() => """
        @echo off
        setlocal
        set "TARGET=%DUCOM_UPDATE_TARGET%"
        set "STAGED=%DUCOM_UPDATE_STAGED%"
        set "PID=%DUCOM_UPDATE_PID%"
        :wait_loop
        tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
        if not errorlevel 1 (
            timeout /t 1 /nobreak >nul
            goto wait_loop
        )
        if exist "%STAGED%.part" del /q "%STAGED%.part"
        move /y "%STAGED%" "%TARGET%" >nul 2>&1
        if exist "%STAGED%" (
            timeout /t 2 /nobreak >nul
            move /y "%STAGED%" "%TARGET%" >nul 2>&1
        )
        if exist "%TARGET%" start "" "%TARGET%"
        endlocal
        (goto) 2>nul & del "%~f0"
        """;
}
