using System.ComponentModel;
using System.Windows;
using DuCom.Core.Sending;
using DuCom.Services;
using DuCom.ViewModels;

namespace DuCom;

public partial class FloatSendWindow
{
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsOpen) && !_session.IsOpen)
        {
            // The port session closed: the per-port float window closes with it.
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        SavePreferences();
        LogList.ItemsSource = null;
        // The owner may already be closing; unregister directly without asking it to
        // perform another window operation during this window's close notification.
        if (Application.Current.MainWindow?.DataContext is MainViewModel viewModel)
        {
            viewModel.FloatSendClosedFromWindow(PortName);
        }

        _ = DisposeAsync().AsTask();
        base.OnClosed(e);
    }

    /// <summary>Releases the tap subscription; the command host lives on the session view model. Safe to repeat.</summary>
    public async ValueTask DisposeAsync()
    {
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _session.UnregisterDisplayTap(TapId);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        // Non-blocking: the async path releases the tap subscription in the background.
        _ = DisposeAsync().AsTask();
    }

    private void ApplyGeometry(MiniLogPreferences preferences)
    {
        double maximumWidth = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
        double maximumHeight = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
        Width = Math.Clamp(preferences.Width, MinWidth, maximumWidth);
        Height = Math.Clamp(preferences.Height, MinHeight, maximumHeight);
        if (preferences.Left is not double left || preferences.Top is not double top)
        {
            return;
        }

        double clampedLeft = Math.Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        double clampedTop = Math.Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        Left = clampedLeft;
        Top = clampedTop;
    }

    private void SavePreferences()
    {
        SendMode sendMode = SendHexToggle.IsChecked == true ? SendMode.Hex : SendMode.Str;
        NewlinePolicy newline = NewlineToggle.IsChecked == true ? NewlinePolicy.CrLf : NewlinePolicy.None;
        _preferences = new MiniLogPreferences(
            FiniteOrNull(RestoreBounds.Left),
            FiniteOrNull(RestoreBounds.Top),
            FiniteOrDefault(RestoreBounds.Width, Width),
            FiniteOrDefault(RestoreBounds.Height, Height),
            Topmost,
            PinLogToggle.IsChecked != true,
            sendMode,
            newline);
        MiniLogPreferencesService.Save(PortName, _preferences);
    }

    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;

    private static double FiniteOrDefault(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}
