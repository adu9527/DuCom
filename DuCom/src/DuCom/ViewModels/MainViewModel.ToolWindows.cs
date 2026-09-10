using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
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
        SessionViewModel? session = SelectedSession ?? SelectedRightSession;
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
        session ??= SelectedSession ?? SelectedRightSession;
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
}
