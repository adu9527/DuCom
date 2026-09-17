using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private LogAnalyzerWindow? _logAnalyzerWindow;
    private ProtocolDecoderWindow? _protocolDecoderWindow;
    private VariablePlotWindow? _variablePlotWindow;

    [ObservableProperty]
    public partial bool IsProtocolDecoderOpen { get; private set; }

    [ObservableProperty]
    public partial bool IsVariablePlotOpen { get; private set; }

    private readonly DuCom.Core.Presenting.PortWindowRegistry<FloatSendWindow> _floatSendWindows = new(
        window =>
        {
            if (window.IsLoaded)
            {
                window.Activate();
            }
        },
        window => window.Close());

    private readonly DuCom.Core.Presenting.PortWindowRegistry<LogFilterWindow> _logFilterWindows = new(
        window =>
        {
            if (window.IsLoaded)
            {
                window.Activate();
            }
        },
        window => window.Close());

    [RelayCommand]
    private void ToggleFloatSend()
    {
        SessionViewModel? session = Workspace.SelectedSession ?? Workspace.SelectedRightSession;
        if (session is null)
        {
            StatusMessage = GetResourceString("Status.NoSessionSelected");
            return;
        }

        _floatSendWindows.GetOrOpen(session.PortName, _ =>
        {
            FloatSendWindow window = new(session) { Owner = Application.Current.MainWindow };
            window.Show();
            Program.DiagnosticLog?.Information($"Float send window opened. Port={session.PortName}");
            return window;
        });
    }

    [RelayCommand]
    private void ShowLogFilter(SessionViewModel? session)
    {
        session ??= Workspace.SelectedSession ?? Workspace.SelectedRightSession;
        if (session is null)
        {
            StatusMessage = GetResourceString("Status.NoSessionSelected");
            return;
        }

        _logFilterWindows.GetOrOpen(session.PortName, _ =>
        {
            LogFilterWindow window = new(session) { Owner = Application.Current.MainWindow };
            window.Show();
            Program.DiagnosticLog?.Information($"Log filter window opened. Port={session.PortName}");
            return window;
        });
    }

    internal void CloseFloatSendFor(string portName) => _floatSendWindows.Close(portName);

    internal void CloseLogFilterFor(string portName) => _logFilterWindows.Close(portName);

    /// <summary>Called by the window layer when the user closes a float send window directly.</summary>
    public void FloatSendClosedFromWindow(string portName) => _floatSendWindows.Remove(portName);

    /// <summary>Called by the window layer when the user closes a log filter window directly.</summary>
    public void LogFilterClosedFromWindow(string portName) => _logFilterWindows.Remove(portName);

    /// <summary>
    /// Applies a reply-window duration edited in one float send window to every other open
    /// float send window, matching the reference tool's behavior.
    /// </summary>
    internal void ApplyReplyWindowToFloatSends(FloatSendWindow source, int milliseconds)
    {
        foreach (FloatSendWindow window in _floatSendWindows.Windows)
        {
            if (!ReferenceEquals(window, source))
            {
                window.SetReplyWindowMs(milliseconds);
            }
        }
    }

    [RelayCommand]
    private void ShowToolCenter(string page) =>
        new ToolCenterWindow(page, ShortcutManager, this, Telnet) { Owner = Application.Current.MainWindow }.Show();

    [RelayCommand]
    private void ShowCommandGroups() =>
        new CommandGroupsWindow(CommandRunner, this) { Owner = Application.Current.MainWindow }.Show();

    [RelayCommand]
    private void ShowLogAnalyzer()
    {
        if (_logAnalyzerWindow is { IsLoaded: true })
        {
            _logAnalyzerWindow.Activate();
            return;
        }

        _logAnalyzerWindow = new LogAnalyzerWindow(Workspace, LogAnalyzerRulesFilePath)
        {
            Owner = Application.Current.MainWindow,
        };
        _logAnalyzerWindow.Closed += (_, _) => _logAnalyzerWindow = null;
        _logAnalyzerWindow.Show();
        Program.DiagnosticLog?.Information("Log analyzer window opened.");
    }

    [RelayCommand]
    private void ToggleProtocolDecoder()
    {
        if (_protocolDecoderWindow is { } open)
        {
            open.Close();
            return;
        }

        ProtocolDecoderWindow? window = null;
        try
        {
            window = new ProtocolDecoderWindow(Workspace, _analysisWindowPreferences) { Owner = Application.Current.MainWindow };
            ProtocolDecoderWindow captured = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_protocolDecoderWindow, captured))
                {
                    _protocolDecoderWindow = null;
                    IsProtocolDecoderOpen = false;
                }
            };
            window.Show();
            _protocolDecoderWindow = window;
            IsProtocolDecoderOpen = true;
            Program.DiagnosticLog?.Information("Protocol decoder window opened.");
        }
        catch (Exception exception)
        {
            window?.Close();
            IsProtocolDecoderOpen = false;
            Program.DiagnosticLog?.Error("Protocol decoder window failed to open.", exception);
            StatusMessage = GetResourceString("Analysis.OpenFailed");
        }
    }

    [RelayCommand]
    private void ToggleVariablePlot()
    {
        if (_variablePlotWindow is { } open)
        {
            open.Close();
            return;
        }

        VariablePlotWindow? window = null;
        try
        {
            window = new VariablePlotWindow(VariableMonitor, ApplyMonitorConfiguration, _analysisWindowPreferences) { Owner = Application.Current.MainWindow };
            VariablePlotWindow captured = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_variablePlotWindow, captured))
                {
                    _variablePlotWindow = null;
                    IsVariablePlotOpen = false;
                }
            };
            window.Show();
            _variablePlotWindow = window;
            IsVariablePlotOpen = true;
            Program.DiagnosticLog?.Information("Variable plot window opened.");
        }
        catch (Exception exception)
        {
            window?.Close();
            IsVariablePlotOpen = false;
            Program.DiagnosticLog?.Error("Variable plot window failed to open.", exception);
            StatusMessage = GetResourceString("Analysis.OpenFailed");
        }
    }
}
