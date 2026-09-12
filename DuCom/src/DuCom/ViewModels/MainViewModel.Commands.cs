using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Diagnostics;
using DuCom.Core.Sending;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanOpen))] private Task OpenAsync() => Workspace.OpenPortAsync(SelectedPort!, true);
    private bool CanOpen() => !string.IsNullOrWhiteSpace(SelectedPort);

    [RelayCommand] private void ClearDisplay() => Workspace.ClearActiveDisplay();

    [RelayCommand] private void FormatJson() => Workspace.FormatJson();
    [RelayCommand] private void JoinLines() => Workspace.JoinLines();

    [RelayCommand]
    private Task LoadSendFileAsync(SessionViewModel? session) =>
        Workspace.LoadSendFileAsync(session ?? SerialParameters.TargetSession as SessionViewModel);

    [RelayCommand]
    private static void OpenApplicationFolder() =>
        Process.Start(new ProcessStartInfo(AppContext.BaseDirectory) { UseShellExecute = true });

    [RelayCommand]
    private static void ExitApplication()
    {
        if (Application.Current.MainWindow is MainWindow window)
        {
            window.RequestExit();
        }
    }

    [RelayCommand]
    private static void OpenDiagnosticFolder() => SystemLogAccess.OpenCurrent();

    [RelayCommand]
    private static void OpenDocumentation()
    {
        string language = ((App)Application.Current).CurrentLanguage;
        string url = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase)
            ? EnglishUserManualUrl
            : ChineseUserManualUrl;
        OpenUrl(url);
    }

    [RelayCommand]
    private static void ShowAbout()
    {
        AboutWindow window = new() { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    [RelayCommand]
    private static void CheckForUpdates()
    {
        Services.Updates.UpdateFlow.EnsureDownloadPromptSubscription();
        UpdateWindow.Show(Application.Current.MainWindow);
    }

    [RelayCommand]
    private static void OpenFeedback()
    {
        FeedbackWindow window = new() { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    [RelayCommand]
    private void ToggleBottomSend() => IsBottomSendVisible = !IsBottomSendVisible;

    [RelayCommand]
    private void ToggleFollowEnd()
    {
        Workspace.ToggleActiveFollowEnd();
    }

    [RelayCommand]
    private void ToggleDefaultReceiveMode()
    {
        Workspace.ToggleActiveReceiveMode();
    }

    [RelayCommand]
    private void ToggleDefaultTimestamp()
    {
        Workspace.ToggleActiveTimestamp();
    }

    [RelayCommand]
    private void ToggleSelectedSendMode()
    {
        Workspace.ToggleActiveSendMode();
    }

    private void NotifyCommandStates()
    {
        OpenCommand.NotifyCanExecuteChanged();
        Workspace.NotifyCommandStatesFromMain();
    }
    private void NotifyOpenCommandState() => OpenCommand.NotifyCanExecuteChanged();
}
