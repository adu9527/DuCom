using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Services.Updates;

namespace DuCom.ViewModels;

public partial class UpdateViewModel : ObservableObject
{
    public static UpdateViewModel Instance { get; } = new();

    private readonly AppUpdateService _service = AppUpdateService.Instance;
    private CompositeFormat? _failedFormat;
    private CompositeFormat? _availableFormat;
    private CompositeFormat? _downloadingFormat;
    private CompositeFormat? _skippedFormat;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial bool IsIndeterminate { get; private set; }

    [ObservableProperty]
    public partial string CurrentVersion { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LatestVersion { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ChannelText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ReleaseNotes { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasReleaseNotes { get; private set; }

    [ObservableProperty]
    public partial bool CanCheck { get; private set; }

    [ObservableProperty]
    public partial bool CanDownload { get; private set; }

    [ObservableProperty]
    public partial bool CanApply { get; private set; }

    [ObservableProperty]
    public partial bool CanSkip { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressVisible { get; private set; }

    public string ReleasePageUrl => _service.ReleasePageUrl;

    private UpdateViewModel()
    {
        _service.PropertyChanged += OnServicePropertyChanged;
        RefreshState();
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (_service.Phase == UpdatePhase.ReadyToInstall)
        {
            UpdateFlow.PromptApplyAndRestart();
            return;
        }

        bool hasUpdate = await _service.CheckForUpdatesAsync();
        if (!hasUpdate && _service.Phase == UpdatePhase.Failed)
        {
            ThemedMessageDialog.Show(
                Application.Current?.MainWindow,
                string.Format(CultureInfo.InvariantCulture, FailedFormat, _service.LastError),
                GetResourceString("Update.Title"),
                ThemedMessageDialogKind.Error);
        }

        RefreshState();
    }

    [RelayCommand]
    private Task DownloadAsync() => _service.DownloadLatestAsync();

    [RelayCommand]
    private static void ApplyAndRestart() => AppUpdateService.Instance.ApplyAndRestart();

    [RelayCommand]
    private void SkipVersion()
    {
        _service.SkipCurrentVersion();
        RefreshState();
    }

    [RelayCommand]
    private static void OpenReleasePage() =>
        Process.Start(new ProcessStartInfo(AppUpdateService.Instance.ReleasePageUrl) { UseShellExecute = true });

    private void OnServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshState();

    private void RefreshState()
    {
        CurrentVersion = _service.CurrentVersionText;
        LatestVersion = string.IsNullOrWhiteSpace(_service.LatestVersionTag) ? "—" : _service.LatestVersionTag;
        ChannelText = _service.UsesVelopack
            ? GetResourceString("Update.Channel.Installed")
            : GetResourceString("Update.Channel.Portable");
        ReleaseNotes = _service.ReleaseNotes ?? string.Empty;
        HasReleaseNotes = !string.IsNullOrWhiteSpace(ReleaseNotes);

        CanCheck = _service.Phase is not (UpdatePhase.Checking or UpdatePhase.Downloading);
        CanDownload = _service.Phase is (UpdatePhase.Available or UpdatePhase.Failed) && _service.HasPendingUpdate;
        CanApply = _service.Phase == UpdatePhase.ReadyToInstall;
        CanSkip = _service.Phase == UpdatePhase.Available;
        IsProgressVisible = _service.Phase == UpdatePhase.Downloading;
        IsIndeterminate = _service.IsDownloadProgressIndeterminate;
        ProgressValue = _service.DownloadProgress * 100d;
        StatusText = _service.Phase switch
        {
            UpdatePhase.Checking => GetResourceString("Update.Status.Checking"),
            UpdatePhase.UpToDate => GetResourceString("Update.Status.UpToDate"),
            UpdatePhase.Available => string.Format(CultureInfo.InvariantCulture, AvailableFormat, _service.LatestVersionTag),
            UpdatePhase.Downloading => string.Format(
                CultureInfo.InvariantCulture,
                DownloadingFormat,
                IsIndeterminate ? "…" : $"{ProgressValue:0}%"),
            UpdatePhase.ReadyToInstall => GetResourceString("Update.Status.ReadyToInstall"),
            UpdatePhase.Failed => string.Format(CultureInfo.InvariantCulture, FailedFormat, _service.LastError),
            _ => _service.IsLatestVersionSkipped()
                ? string.Format(CultureInfo.InvariantCulture, SkippedFormat, _service.LatestVersionTag)
                : GetResourceString("Update.Status.Idle"),
        };
    }

    private CompositeFormat FailedFormat => _failedFormat ??= CompositeFormat.Parse(GetResourceString("Update.Status.Failed"));

    private CompositeFormat AvailableFormat => _availableFormat ??= CompositeFormat.Parse(GetResourceString("Update.Status.Available"));

    private CompositeFormat DownloadingFormat => _downloadingFormat ??= CompositeFormat.Parse(GetResourceString("Update.Status.Downloading"));

    private CompositeFormat SkippedFormat => _skippedFormat ??= CompositeFormat.Parse(GetResourceString("Update.Status.Skipped"));

    private static string GetResourceString(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
