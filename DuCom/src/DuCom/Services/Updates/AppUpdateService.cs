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
        if (_stager.HasStagedPackage)
        {
            Phase = UpdatePhase.ReadyToInstall;
            LatestVersionTag = ReadStagedPackageVersion() ?? string.Empty;
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
                File.WriteAllText(Path.Combine(_stager.StagingDirectory, "staged-version.txt"), LatestVersionTag);
            }

            Phase = UpdatePhase.ReadyToInstall;
            if (_updateManager is { IsInstalled: true })
            {
                // Installed builds update fully automatically: apply and restart right away.
                ApplyAndRestart();
            }
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

    public void ApplyAndRestart()
    {
        if (Phase != UpdatePhase.ReadyToInstall)
        {
            return;
        }

        if (_updateManager is { IsInstalled: true } manager)
        {
            manager.ApplyUpdatesAndRestart(_updateInfo!.TargetFullRelease, []);
            return;
        }

        _stager.ApplyStagedUpdate();
        System.Windows.Application.Current.Shutdown();
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
            string marker = Path.Combine(_stager.StagingDirectory, "staged-version.txt");
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
