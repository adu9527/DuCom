using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Diagnostics;
using DuCom.Core.Ports;
using DuCom.Core.Sending;
using DuCom.Services;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync() => OpenSelectedPortAsync(showFailureDialog: true);

    private async Task OpenSelectedPortAsync(bool showFailureDialog)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string portName = SelectedPort!;
        SessionViewModel? previouslySelectedSession = SelectedSession;
        SessionViewModel? session = Sessions.FirstOrDefault(
            item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        if (session is { IsOpen: false })
        {
            session = await RebuildClosedSessionAsync(session);
        }
        bool createdNew = false;
        if (session is null)
        {
            try
            {
                SerialPortSettings defaults = SerialPortSettings.Default(portName);
                SerialPortSettings settings = defaults with
                {
                    BaudRate = BaudRate,
                    DataBits = DataBits,
                    StopBits = StopBits,
                    Parity = Parity,
                    Handshake = Handshake,
                    EncodingName = EncodingName,
                    DtrEnable = false,
                    RtsEnable = false,
                    DiscardNull = false,
                };
                if (_portOverrides.TryGetValue(portName, out PortSettingSnapshot? overrideValues))
                {
                    settings = settings with
                    {
                        BaudRate = overrideValues.BaudRate,
                        DataBits = overrideValues.DataBits,
                        StopBits = overrideValues.StopBits,
                        Parity = overrideValues.Parity,
                        Handshake = overrideValues.Handshake,
                        EncodingName = overrideValues.EncodingName,
                        DtrEnable = overrideValues.DtrEnable ?? settings.DtrEnable,
                        RtsEnable = overrideValues.RtsEnable ?? settings.RtsEnable,
                        DiscardNull = overrideValues.DiscardNull ?? settings.DiscardNull,
                    };
                }
                PortSessionPreferences preferences = ResolvePortPreferences(portName);
                session = CreateSession(settings, preferences);
                session.AutoReconnect = overrideValues?.AutoReconnect ?? false;
                Sessions.Add(session);
                createdNew = true;
                Program.DiagnosticLog?.Information(
                    $"Session created. Port={portName}; Baud={settings.BaudRate}; DataBits={settings.DataBits}; StopBits={settings.StopBits}; Parity={settings.Parity}; Handshake={settings.Handshake}; Encoding={settings.EncodingName}; ReceiveMode={preferences.ReceiveMode}; Timestamp={preferences.TimestampEnabled}; Logging={preferences.LoggingEnabled}; LogDirectory={preferences.LogDirectory}");
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Error($"Invalid port settings. Port={portName}; {exception.Message}");
                StatusMessage = GetResourceString("Status.InvalidPortSettings");
                return;
            }
        }

        bool sessionIsInRightPane = RightSessions.Contains(session);
        if (sessionIsInRightPane)
        {
            SelectedRightSession = session;
        }

        PortCommandResult result = await session.OpenAsync();
        if (!Sessions.Contains(session))
        {
            Program.DiagnosticLog?.Information(
                $"Ignored late open result after session tab was closed. Port={portName}; Result={result}");
            NotifyCommandStates();
            return;
        }

        if (result != PortCommandResult.Succeeded)
        {
            if (!sessionIsInRightPane &&
                previouslySelectedSession is not null &&
                !ReferenceEquals(previouslySelectedSession, session) &&
                Sessions.Contains(previouslySelectedSession))
            {
                SelectedSession = previouslySelectedSession;
            }
            else if (!sessionIsInRightPane)
            {
                SelectedSession = session;
            }

            StatusMessage = GetResourceString("Status.OpenFailed")
                .Replace("{0}", session.FaultMessage, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning(
                $"Open failed. Port={portName}; Result={result}; Fault={session.FaultMessage}; CreatedNew={createdNew}");
            if (showFailureDialog && ThemedMessageDialog.ShowOpenFailure(
                    Application.Current.MainWindow,
                    StatusMessage,
                    GetResourceString("Connection.OpenFailedTitle"),
                    session))
            {
                await CloseSessionAsync(session);
            }
        }
        else
        {
            if (!sessionIsInRightPane)
            {
                SelectedSession = session;
            }

            StatusMessage = string.Empty;
            if (session.IsOpen)
            {
                RememberPortOverride(portName);
            }

            Program.DiagnosticLog?.Information(
                $"Open command completed. Port={portName}; Result={result}; IsOpen={session.IsOpen}; Fault={session.FaultMessage}");
        }

        NotifyCommandStates();
        stopwatch.Stop();
        if (stopwatch.Elapsed >= SlowOperationThreshold)
        {
            Program.DiagnosticLog?.Warning(
                $"Slow open command. Port={portName}; ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.0}; IsOpen={session.IsOpen}");
        }
    }

    private bool CanOpen() => !string.IsNullOrWhiteSpace(SelectedPort);

    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseAsync()
    {
        SessionViewModel session = SelectedSession!;
        string logFilePath = session.WorkspaceSession.CurrentLogFilePath ?? string.Empty;
        Stopwatch stopwatch = Stopwatch.StartNew();
        await session.CloseAsync();
        stopwatch.Stop();
        Program.DiagnosticLog?.Information(
            $"Close command completed. Port={session.PortName}; CompletedAt={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}; ElapsedMs={stopwatch.Elapsed.TotalMilliseconds:0.0}; IsOpen={session.IsOpen}; Fault={session.FaultMessage}; LogFile={logFilePath}");
        NotifyCommandStates();
    }

    private bool CanClose() => SelectedSession is { IsOpen: true, IsBusy: false };

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        SessionViewModel session = SelectedSession!;
        try
        {
            await session.SendAsync();
            if (FreezeAfterSend)
            {
                session.FollowEnd = false;
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            StatusMessage = GetResourceString("Status.InvalidHex")
                .Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Send rejected. Port={session.PortName}; {exception.Message}");
            return;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            StatusMessage = GetResourceString("Status.SendFailed")
                .Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Send failed. Port={session.PortName}.", exception);
            NotifyCommandStates();
            return;
        }

        StatusMessage = string.Empty;
        if (_sendHistory.Record(session.SendText))
        {
            try
            {
                SendHistoryFileService.Save(_sendHistory);
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Warning("Failed to save send history.", exception);
            }
        }

        _sendHistoryNavigator.Reset();
        NotifyCommandStates();
    }

    private bool CanSend() => SelectedSession is { IsOpen: true, IsBusy: false };

    [RelayCommand]
    private void ClearDisplay() => ClearActiveDisplay();

    [RelayCommand]
    private void FormatJson()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null || string.IsNullOrWhiteSpace(session.SendText))
        {
            return;
        }

        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(session.SendText);
            string formatted = JsonSerializer.Serialize(document.RootElement, ConfigurationJsonOptions);
            session.SendText = formatted;
            StatusMessage = string.Empty;
            Program.DiagnosticLog?.Information($"Formatted JSON in send editor. Port={session.PortName}.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            StatusMessage = GetResourceString("Status.InvalidJson")
                .Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"JSON formatting failed. Port={session.PortName}; {exception.Message}");
        }
    }

    [RelayCommand]
    private void JoinLines()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        string normalized = session.SendText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\n', ' ');
        session.SendText = normalized;
    }

    [RelayCommand]
    private async Task LoadSendFileAsync(SessionViewModel? session)
    {
        session ??= _portSettingsTargetSession ?? SelectedSession ?? SelectedRightSession;
        if (session is null)
        {
            return;
        }

        OpenFileDialog dialog = new() { Filter = GetResourceString("Send.FileFilter"), CheckFileExists = true };
        if (dialog.ShowDialog() == true)
        {
            session.SendText = await File.ReadAllTextAsync(dialog.FileName, Encoding.UTF8);
        }
    }

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
        ToggleActiveFollowEnd();
    }

    [RelayCommand]
    private void ToggleDefaultReceiveMode()
    {
        ToggleActiveReceiveMode();
    }

    [RelayCommand]
    private void ToggleDefaultTimestamp()
    {
        ToggleActiveTimestamp();
    }

    [RelayCommand]
    private void ToggleSelectedSendMode()
    {
        if (ActiveSession is { } session)
        {
            session.SendMode = session.SendMode == SendMode.Str ? SendMode.Hex : SendMode.Str;
            RememberPortOverride(session.PortName);
        }
    }
}
