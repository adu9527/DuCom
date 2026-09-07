using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using DuCom.ViewModels;

namespace DuCom.Services.Updates;

/// <summary>UI orchestration for update prompts: silent startup checks and download-complete notifications.</summary>
public static class UpdateFlow
{
    private static bool _downloadPromptSubscribed;
    private static CompositeFormat? _newVersionFormat;

    public static void EnsureDownloadPromptSubscription()
    {
        if (_downloadPromptSubscribed)
        {
            return;
        }

        _downloadPromptSubscribed = true;
        AppUpdateService.Instance.PropertyChanged += OnServicePropertyChanged;
    }

    public static async Task RunAutomaticCheckAsync()
    {
        EnsureDownloadPromptSubscription();
        await Task.Delay(TimeSpan.FromSeconds(8));

        if (Application.Current?.MainWindow is not { DataContext: MainViewModel viewModel })
        {
            return;
        }

        // Runs regardless of AutoCheckUpdates: a stale staged package must not be
        // installable, and dropping it never prompts anything on its own.
        await AppUpdateService.Instance.ValidateStagedPackageAsync();

        if (!viewModel.AutoCheckUpdates || UpdateWindow.IsOpen)
        {
            return;
        }

        AppUpdateService service = AppUpdateService.Instance;
        if (service.Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Available or UpdatePhase.ReadyToInstall)
        {
            return;
        }

        if (!await service.CheckForUpdatesAsync() || service.IsLatestVersionSkipped())
        {
            return;
        }

        ThemedMessageDialogChoice choice = ThemedMessageDialog.ShowChoice(
            Application.Current.MainWindow,
            string.Format(CultureInfo.InvariantCulture, NewVersionFormat, service.LatestVersionTag),
            GetResourceString("Update.Title"),
            ThemedMessageDialogKind.Information,
            "Update.Prompt.UpdateNow",
            "Update.SkipVersion");
        if (choice == ThemedMessageDialogChoice.Primary)
        {
            UpdateWindow.Show(Application.Current.MainWindow, downloadOnLoad: true);
        }
        else if (choice == ThemedMessageDialogChoice.Secondary)
        {
            service.SkipCurrentVersion();
        }
    }

    private static void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppUpdateService.Phase) ||
            AppUpdateService.Instance.Phase != UpdatePhase.ReadyToInstall)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(PromptApplyAndRestart);
    }

    /// <summary>Prompts the portable user to swap the staged executable and restart.</summary>
    internal static void PromptApplyAndRestart()
    {
        Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;
        ThemedMessageDialogChoice choice = ThemedMessageDialog.ShowChoice(
            owner,
            GetResourceString("Update.Prompt.Ready"),
            GetResourceString("Update.Title"),
            ThemedMessageDialogKind.Information,
            "Update.ApplyAndRestart",
            "Update.Prompt.Later");
        if (choice == ThemedMessageDialogChoice.Primary)
        {
            AppUpdateService.Instance.ApplyAndRestart();
        }
    }

    private static CompositeFormat NewVersionFormat =>
        _newVersionFormat ??= CompositeFormat.Parse(GetResourceString("Update.Prompt.NewVersion"));

    private static string GetResourceString(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
