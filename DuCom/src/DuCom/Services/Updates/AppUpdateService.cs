using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace DuCom.Services.Updates;

public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Failed,
}

/// <summary>
/// Single shared update orchestrator. Builds installed through Velopack update through
/// <see cref="UpdateManager"/>; a portable single-file executable updates by downloading the
/// release asset and swapping itself through a staged apply script.
/// </summary>
public sealed partial class AppUpdateService : ObservableObject, IDisposable
{
    public const string RepositoryUrl = "https://github.com/adu9527/DuCom";
    public const string PortableAssetName = "DuCom.exe";

    private readonly GitHubReleaseClient _releaseClient = new(RepositoryUrl);
    private readonly PortableUpdateStager _stager = new();
    private readonly UpdateManager? _updateManager;
    private readonly Version _currentVersion;
    private GitHubRelease? _latestRelease;
    private UpdateInfo? _updateInfo;
    private Func<string?>? _getSkippedVersion;
    private Action<string?>? _setSkippedVersion;

    public static AppUpdateService Instance { get; } = new();

    [ObservableProperty]
    public partial UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;

    [ObservableProperty]
    public partial double DownloadProgress { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloadProgressIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string LatestVersionTag { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ReleaseNotes { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ReleasePageUrl { get; private set; } = $"{RepositoryUrl}/releases";

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    public string CurrentVersionText => UpdateVersion.ToDisplayString(_currentVersion);

    public bool UsesVelopack => _updateManager is { IsInstalled: true };

    /// <summary>True when the portable channel has a backed-up executable ready to restore.</summary>
    public bool CanRollbackPortable => _updateManager is not { IsInstalled: true } && _stager.HasRollbackBackup;

    public bool HasPendingUpdate => _updateInfo is not null || _latestRelease is not null;

    private AppUpdateService()
    {
        _currentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
        UpdateManager? manager = null;
        try
        {
            manager = new UpdateManager(new GithubSource(RepositoryUrl, null, false));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Velopack update manager is unavailable. {exception.Message}");
        }

        _updateManager = manager;
        foreach (string marker in _stager.ConsumeApplyMarkers())
        {
            Program.DiagnosticLog?.Warning($"Previous update apply did not complete ({marker}). The staged package is kept for retry.");
        }

        if (_stager.HasStagedPackage)
        {
            Version? stagedVersion = UpdateVersion.TryParse(ReadStagedPackageVersion());
            if (stagedVersion is not null && stagedVersion <= _currentVersion)
            {
                // The staged package is not newer than what is already running (for example
                // the user updated through the installer meanwhile); it is stale, drop it.
                _stager.DiscardStagedPackage();
            }
            else
            {
                Phase = UpdatePhase.ReadyToInstall;
                LatestVersionTag = ReadStagedPackageVersion() ?? string.Empty;
            }
        }
    }

    public void AttachSettings(Func<string?> getSkippedVersion, Action<string?> setSkippedVersion)
    {
        _getSkippedVersion = getSkippedVersion;
        _setSkippedVersion = setSkippedVersion;
    }

    public bool IsLatestVersionSkipped()
    {
        string? skipped = _getSkippedVersion?.Invoke();
        if (string.IsNullOrWhiteSpace(skipped) || string.IsNullOrWhiteSpace(LatestVersionTag))
        {
            return false;
        }

        Version? skippedVersion = UpdateVersion.TryParse(skipped);
        Version? latestVersion = UpdateVersion.TryParse(LatestVersionTag);
        return skippedVersion is not null && skippedVersion.Equals(latestVersion);
    }

    public async Task<bool> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        Phase = UpdatePhase.Checking;
        LastError = null;
        try
        {
            bool hasUpdate;
            if (_updateManager is { IsInstalled: true } manager)
            {
                _updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(true);
                hasUpdate = _updateInfo is not null;
                if (_updateInfo?.TargetFullRelease is { } target)
                {
                    LatestVersionTag = $"V{target.Version}";
                    ReleaseNotes = ReleaseNotesSanitizer.ToPlainText(target.NotesMarkdown ?? target.NotesHTML);
                    ReleasePageUrl = $"{RepositoryUrl}/releases/tag/{LatestVersionTag}";
                }
            }
            else
            {
                _latestRelease = await _releaseClient.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(true)
                    ?? throw new InvalidOperationException("No published release was found on GitHub.");
                LatestVersionTag = _latestRelease.TagName;
                ReleaseNotes = ReleaseNotesSanitizer.ToPlainText(_latestRelease.Body);
                ReleasePageUrl = _latestRelease.HtmlUrl ?? $"{RepositoryUrl}/releases";
                Version? latest = UpdateVersion.TryParse(_latestRelease.TagName)
                    ?? throw new InvalidOperationException($"Release tag '{_latestRelease.TagName}' is not a recognizable version.");
                hasUpdate = UpdateVersion.IsNewer(latest, _currentVersion);
            }

            Phase = hasUpdate ? UpdatePhase.Available : UpdatePhase.UpToDate;
            return hasUpdate;
        }
        catch (OperationCanceledException)
        {
            Phase = UpdatePhase.Idle;
            throw;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Update check failed. {exception.Message}");
            LastError = exception.Message;
            Phase = UpdatePhase.Failed;
            return false;
        }
    }

    public async Task DownloadLatestAsync(CancellationToken cancellationToken = default)
    {
        if (Phase is not (UpdatePhase.Available or UpdatePhase.Failed) || !HasPendingUpdate)
        {
            return;
        }

        Phase = UpdatePhase.Downloading;
        LastError = null;
        IsDownloadProgressIndeterminate = true;
        DownloadProgress = 0;
        try
        {
            if (_updateManager is { IsInstalled: true } manager)
            {
                if (_updateInfo is null)
                {
                    throw new InvalidOperationException("No update info was fetched from Velopack.");
                }

                await manager.DownloadUpdatesAsync(_updateInfo, percent => OnUi(() =>
                {
                    IsDownloadProgressIndeterminate = false;
                    DownloadProgress = Math.Clamp(percent / 100d, 0d, 1d);
                }), cancellationToken).ConfigureAwait(true);
            }
            else
            {
                GitHubReleaseAsset? asset = _latestRelease is null
                    ? null
                    : PortableAssetSelector.Select(_latestRelease.Assets ?? [], PortableAssetName);
                if (asset is null)
                {
                    throw new InvalidOperationException($"The release does not contain a portable '{PortableAssetName}' asset.");
                }

                long totalBytes = asset.Size;
                await _stager.StagePackageAsync(_releaseClient, asset, new Progress<long>(received => OnUi(() =>
                {
                    IsDownloadProgressIndeterminate = totalBytes <= 0;
                    DownloadProgress = totalBytes > 0 ? Math.Clamp(received / (double)totalBytes, 0d, 1d) : 0d;
                })), cancellationToken).ConfigureAwait(true);
                _stager.WriteStagedVersionMarker(LatestVersionTag);
            }

            // Both channels stop at ReadyToInstall and ask the user before restarting:
            // a serial tool must never be killed mid-transfer by a silent self-update.
            Phase = UpdatePhase.ReadyToInstall;
        }
        catch (OperationCanceledException)
        {
            Phase = UpdatePhase.Available;
            throw;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Update download failed. {exception.Message}");
            LastError = exception.Message;
            Phase = UpdatePhase.Failed;
        }
    }

    /// <summary>
    /// Drops the staged portable package when a newer release has been published since it
    /// was downloaded, so the user can never install a version older than the latest one.
    /// Network failures keep the staged package (fail-soft).
    /// </summary>
    public async Task ValidateStagedPackageAsync(CancellationToken cancellationToken = default)
    {
        if (Phase != UpdatePhase.ReadyToInstall || _updateManager is { IsInstalled: true })
        {
            return;
        }

        Version? stagedVersion = UpdateVersion.TryParse(ReadStagedPackageVersion());
        if (stagedVersion is null)
        {
            return;
        }

        try
        {
            GitHubRelease? latest = await _releaseClient.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(true);
            Version? latestVersion = latest is null ? null : UpdateVersion.TryParse(latest.TagName);
            if (latestVersion is not null && latestVersion > stagedVersion)
            {
                _stager.DiscardStagedPackage();
                Phase = UpdatePhase.Idle;
                Program.DiagnosticLog?.Information($"Discarded staged update {UpdateVersion.ToDisplayString(stagedVersion)} because {UpdateVersion.ToDisplayString(latestVersion)} is available.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Staged-update validation failed. {exception.Message}");
        }
    }

    public void ApplyAndRestart()
    {
        if (Phase != UpdatePhase.ReadyToInstall)
        {
            return;
        }

        BackupUserDataBeforeUpdate();
        if (_updateManager is { IsInstalled: true } manager)
        {
            manager.ApplyUpdatesAndRestart(_updateInfo!.TargetFullRelease, []);
            return;
        }

        _stager.ApplyStagedUpdate();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// Restores the portable executable backed up by the last update and restarts. The
    /// user confirms through the UI before this is called (UpdateViewModel.RollbackAndRestart).
    /// </summary>
    public void RollbackToBackupAndRestart()
    {
        if (!CanRollbackPortable)
        {
            return;
        }

        BackupUserDataBeforeUpdate();
        try
        {
            _stager.ApplyRollback();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Portable rollback failed. {exception.Message}");
            LastError = exception.Message;
            return;
        }

        // Any staged update is now obsolete relative to the rolled-back executable.
        _stager.DiscardStagedPackage();
        Phase = UpdatePhase.Idle;
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// Updates are the moment persisted-data formats change most often, so a user-data
    /// backup is forced right before the swap. A failed backup does not block the update:
    /// the swap itself never touches the user-data directory.
    /// </summary>
    private static void BackupUserDataBeforeUpdate()
    {
        try
        {
            string path = UserDataBackupService.CreateBackup();
            Program.DiagnosticLog?.Information($"Pre-update user-data backup created. Path={path}");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Pre-update user-data backup failed; applying the update anyway.", exception);
        }
    }

    public void SkipCurrentVersion()
    {
        if (!string.IsNullOrWhiteSpace(LatestVersionTag))
        {
            _setSkippedVersion?.Invoke(LatestVersionTag);
        }
    }

    private string? ReadStagedPackageVersion()
    {
        try
        {
            string marker = _stager.StagedVersionMarkerPath;
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void OnUi(Action action)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        _releaseClient.Dispose();
        GC.SuppressFinalize(this);
    }
}
