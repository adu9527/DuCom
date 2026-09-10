using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private async Task ToggleSettingsAsync()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        if (_serialParametersWindow is { IsLoaded: true })
        {
            if (!await FlushPortSettingsAsync())
            {
                _serialParametersWindow.Activate();
                return;
            }

            _allowSerialParametersWindowClose = true;
            _serialParametersWindow.Close();
        }

        ApplyDefaultSettingsToEditor();
        SerialParameterSendMode = DefaultSendMode;
        SerialParameterNewline = DefaultNewline;
        _settingsWindow = new SettingsWindow()
        {
            Owner = Application.Current.MainWindow,
            DataContext = this,
        };
        _settingsWindow.Closed += (_, _) =>
        {
            ShortcutsSettings.CancelPendingEdit();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
    }

    [RelayCommand]
    private void OpenSerialParameters(SessionViewModel? session)
    {
        session ??= SelectedSession;
        if (session is null)
        {
            return;
        }

        if (_serialParametersWindow is { IsLoaded: true })
        {
            _serialParametersWindow.SelectCategory(1);
            _serialParametersWindow.SetWindowTitle(GetResourceString("Settings.SerialParameters"));
            _serialParametersWindow.Activate();
            return;
        }

        _portSettingsTargetSession = session;
        OnPropertyChanged(nameof(IsEditingPortSettings));
        ApplySessionSettingsToEditor(session.WorkspaceSession.Settings);
        SerialParameterReceiveMode = session.ReceiveMode;
        SerialParameterTimestampEnabled = session.TimestampEnabled;
        SerialParameterLoggingEnabled = session.LoggingEnabled;
        SerialParameterFollowEnd = session.FollowEnd;
        SerialParameterFilterEnabled = session.FilterEnabled;
        SerialParameterSendMode = session.SendMode;
        SerialParameterNewline = session.Newline;
        SerialParameterInterpretSendEscapes = session.InterpretSendEscapes;
        SerialParameterTimedSendEnabled = session.TimedSendEnabled;
        SerialParameterTimedSendIntervalMilliseconds = session.TimedSendIntervalMilliseconds;
        SerialParameterAutoReconnect = session.AutoReconnect;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Close();
        }

        _serialParametersWindow = new SettingsWindow(selectedCategory: 1, transportOnly: true)
        {
            Owner = Application.Current.MainWindow,
            DataContext = this,
            Title = GetResourceString("Settings.SerialParameters"),
        };
        _allowSerialParametersWindowClose = false;
        _serialParametersWindow.Closing += OnSerialParametersWindowClosing;
        _serialParametersWindow.Closed += (_, _) =>
        {
            if (_serialParametersWindow is not null)
            {
                _serialParametersWindow.Closing -= OnSerialParametersWindowClosing;
            }

            _serialParametersWindow = null;
            _portSettingsTargetSession = null;
            OnPropertyChanged(nameof(IsEditingPortSettings));
            _portSettingsApplyTimer.Stop();
            _portSettingsApplyPending = false;
            _allowSerialParametersWindowClose = false;
        };
        _serialParametersWindow.Show();
    }

    private async void OnSerialParametersWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowSerialParametersWindowClose)
        {
            return;
        }

        e.Cancel = true;
        if (!await FlushPortSettingsAsync())
        {
            return;
        }

        _allowSerialParametersWindowClose = true;
        if (sender is SettingsWindow window)
        {
            // Closing is still in progress after the awaited settings flush. Queue the
            // final request so WPF receives it only after this notification has returned.
            _ = window.Dispatcher.BeginInvoke(
                window.Close,
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    [RelayCommand]
    private void OpenSendOptions(SessionViewModel? session)
    {
        OpenSerialParameters(session);
        if (_serialParametersWindow is not { IsLoaded: true })
        {
            return;
        }

        _serialParametersWindow.SelectCategory(5);
        _serialParametersWindow.SetWindowTitle(GetResourceString("Send.Options"));
        _serialParametersWindow.Activate();
    }

    [RelayCommand]
    private static void OpenBackupFolder()
    {
        Directory.CreateDirectory(UserDataBackupService.BackupDirectory);
        Process.Start(new ProcessStartInfo(UserDataBackupService.BackupDirectory) { UseShellExecute = true });
    }

    private void ApplySystemBehaviorSettings()
    {
        SystemPowerService.SetPreventSleep(PreventSleep);
        if (!AutoBackupEnabled)
        {
            return;
        }

        DateTimeOffset? latest = UserDataBackupService.GetLatestBackupTime();
        if (latest.HasValue && DateTimeOffset.UtcNow - latest.Value < TimeSpan.FromDays(Math.Max(1, AutoBackupPeriodDays)))
        {
            return;
        }

        try
        {
            string path = UserDataBackupService.CreateBackup();
            Program.DiagnosticLog?.Information($"Automatic user-data backup created. Path={path}");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Automatic user-data backup failed.", exception);
        }
    }
}
