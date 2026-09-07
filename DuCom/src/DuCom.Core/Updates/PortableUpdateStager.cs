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
    private const string BackupVariable = "DUCOM_UPDATE_BACKUP";
    private const string StagedVersionMarkerFileName = "staged-version.txt";
    private const int MaxWaitSeconds = 120;

    private readonly string _targetExecutablePath;

    public string StagingDirectory { get; }

    public string StagedFilePath { get; }

    private string PartFilePath => StagedFilePath + ".part";

    private string ScriptFilePath => Path.Combine(StagingDirectory, "apply-update.cmd");

    private string BackupFilePath => _targetExecutablePath + ".bak";

    private string PreRollbackBackupPath => _targetExecutablePath + ".pre-rollback.bak";

    public string StagedVersionMarkerPath => Path.Combine(StagingDirectory, StagedVersionMarkerFileName);

    public string ApplyFailedMarkerPath => StagedFilePath + ".apply-failed";

    public string ApplyTimeoutMarkerPath => StagedFilePath + ".apply-timeout";

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
        try
        {
            PortablePackageVerifier.Verify(PartFilePath, asset);
        }
        catch
        {
            TryDeleteFile(PartFilePath);
            throw;
        }

        if (File.Exists(StagedFilePath))
        {
            File.Delete(StagedFilePath);
        }

        File.Move(PartFilePath, StagedFilePath);
    }

    /// <summary>Removes the staged package, its partial download, the version marker, and any apply markers.</summary>
    public void DiscardStagedPackage()
    {
        TryDeleteFile(StagedFilePath);
        TryDeleteFile(PartFilePath);
        TryDeleteFile(StagedVersionMarkerPath);
        TryDeleteFile(ApplyFailedMarkerPath);
        TryDeleteFile(ApplyTimeoutMarkerPath);
    }

    /// <summary>
    /// Returns and deletes the markers a previous apply script left behind:
    /// "failed" when the swap could not replace the executable even after retrying,
    /// "timeout" when the running process never exited within the bounded wait.
    /// </summary>
    public List<string> ConsumeApplyMarkers()
    {
        List<string> markers = [];
        if (File.Exists(ApplyFailedMarkerPath))
        {
            markers.Add("failed");
            TryDeleteFile(ApplyFailedMarkerPath);
        }

        if (File.Exists(ApplyTimeoutMarkerPath))
        {
            markers.Add("timeout");
            TryDeleteFile(ApplyTimeoutMarkerPath);
        }

        return markers;
    }

    public void WriteStagedVersionMarker(string versionTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(versionTag);
        File.WriteAllText(StagedVersionMarkerPath, versionTag);
    }

    /// <summary>True when a previous update left a backed-up executable that can be restored.</summary>
    public bool HasRollbackBackup => File.Exists(_targetExecutablePath + ".bak");

    /// <summary>
    /// Swaps the previously backed-up executable back over the running one through the same
    /// staged apply script. The executable being replaced is kept as a
    /// <c>.pre-rollback.bak</c> so an accidental rollback can itself be undone by hand.
    /// </summary>
    public void ApplyRollback()
    {
        if (!HasRollbackBackup)
        {
            throw new InvalidOperationException("No rollback backup was found.");
        }

        if (!PortablePackageVerifier.HasPortableExecutableHeader(_targetExecutablePath + ".bak"))
        {
            throw new IOException($"The rollback backup '{_targetExecutablePath}.bak' does not have a Windows executable (MZ) header.");
        }

        LaunchApplyScript(_targetExecutablePath + ".bak", PreRollbackBackupPath);
    }

    /// <summary>Launches the apply script and returns; the caller is responsible for exiting the process.</summary>
    public void ApplyStagedUpdate()
    {
        if (!HasStagedPackage)
        {
            throw new InvalidOperationException("No staged update package was found.");
        }

        LaunchApplyScript(StagedFilePath, BackupFilePath);
    }

    private void LaunchApplyScript(string stagedPath, string backupPath)
    {
        File.WriteAllText(ScriptFilePath, BuildApplyScript(), Encoding.ASCII);
        ProcessStartInfo startInfo = new("cmd.exe", $"/c \"{ScriptFilePath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = StagingDirectory,
        };
        startInfo.EnvironmentVariables[TargetVariable] = _targetExecutablePath;
        startInfo.EnvironmentVariables[StagedVariable] = stagedPath;
        startInfo.EnvironmentVariables[ProcessIdVariable] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.EnvironmentVariables[BackupVariable] = backupPath;
        Process.Start(startInfo);
    }

    internal static string BuildApplyScript() => $"""
        @echo off
        setlocal
        set "TARGET=%DUCOM_UPDATE_TARGET%"
        set "STAGED=%DUCOM_UPDATE_STAGED%"
        set "PID=%DUCOM_UPDATE_PID%"
        set "BACKUP=%DUCOM_UPDATE_BACKUP%"
        set /a WAITS=0
        :wait_loop
        if %WAITS% GEQ {MaxWaitSeconds} goto wait_timeout
        tasklist /FI "PID eq %PID%" 2>nul | find " %PID% " >nul
        if errorlevel 1 goto do_swap
        set /a WAITS+=1
        timeout /t 1 /nobreak >nul
        goto wait_loop
        :wait_timeout
        echo timeout>"%STAGED%.apply-timeout"
        goto finish
        :do_swap
        if exist "%STAGED%.part" del /q "%STAGED%.part" >nul 2>&1
        if exist "%STAGED%.apply-timeout" del /q "%STAGED%.apply-timeout" >nul 2>&1
        if exist "%STAGED%.apply-failed" del /q "%STAGED%.apply-failed" >nul 2>&1
        if exist "%BACKUP%" del /q "%BACKUP%" >nul 2>&1
        if exist "%TARGET%" copy /y "%TARGET%" "%BACKUP%" >nul 2>&1
        move /y "%STAGED%" "%TARGET%" >nul 2>&1
        if not exist "%STAGED%" goto swap_done
        timeout /t 2 /nobreak >nul
        move /y "%STAGED%" "%TARGET%" >nul 2>&1
        if not exist "%STAGED%" goto swap_done
        echo failed>"%STAGED%.apply-failed"
        if exist "%BACKUP%" del /q "%BACKUP%" >nul 2>&1
        goto finish
        :swap_done
        if exist "%TARGET%" start "" "%TARGET%"
        :finish
        endlocal
        (goto) 2>nul & del "%~f0"
        """;

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: a leftover staged file only delays the next update attempt.
        }
    }
}
