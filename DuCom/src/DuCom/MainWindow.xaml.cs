using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using DuCom.Behaviors;
using DuCom.Services.Shortcuts;
using DuCom.ViewModels;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class MainWindow : FluentWindow
{
    private bool _shutdownCompleted;
    private bool _shutdownStarted;
    private bool _forceExit;
    private readonly ShortcutEngine _shortcutEngine;
    private readonly DispatcherTimer _deviceRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private HwndSource? _windowSource;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        _shortcutEngine = new ShortcutEngine(viewModel.ShortcutManager, viewModel, this);
        Loaded += MainWindow_Loaded;
        viewModel.PropertyChanged += MainViewModel_PropertyChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
        _deviceRefreshTimer.Tick += DeviceRefreshTimer_Tick;
        _processMemoryTimer.Tick += ProcessMemoryTimer_Tick;
        _systemMemoryTimer.Tick += SystemMemoryTimer_Tick;
        viewModel.PluginMenuChanged += RefreshPluginMenu;
        RefreshPluginMenu();
    }

    private void RefreshPluginMenu()
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        PluginMenuContextMenu.Items.Clear();
        System.Windows.Controls.MenuItem manage = new()
        {
            Header = TryFindResource("Menu.Plugins.Manage") as string ?? "插件管理",
            Command = viewModel.ShowPluginManagerCommand,
        };
        PluginMenuContextMenu.Items.Add(manage);
        PluginMenuContextMenu.Items.Add(new Separator());
        IReadOnlyList<Services.Plugins.PluginMenuEntry> entries = viewModel.BuildPluginMenuEntries();
        foreach (Services.Plugins.PluginMenuEntry entry in entries)
        {
            System.Windows.Controls.MenuItem item = new() { Header = entry.Header };
            Services.Plugins.PluginMenuEntry captured = entry;
            item.Click += (_, _) => viewModel.InvokePluginMenu(captured);
            PluginMenuContextMenu.Items.Add(item);
        }

        if (entries.Count == 0)
        {
            System.Windows.Controls.MenuItem placeholder = new()
            {
                Header = (TryFindResource("Menu.Plugins.Empty") as string) ?? "No active plugin menus",
                IsEnabled = false,
            };
            PluginMenuContextMenu.Items.Add(placeholder);
        }

        PluginsMenuButton.Visibility = System.Windows.Visibility.Visible;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmDeviceChange = 0x0219;
        if (message == WmDeviceChange)
        {
            _deviceRefreshTimer.Stop();
            _deviceRefreshTimer.Start();
        }

        return IntPtr.Zero;
    }

    private void DeviceRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _deviceRefreshTimer.Stop();
        if (DataContext is MainViewModel viewModel && viewModel.RefreshPortsCommand.CanExecute(null))
        {
            viewModel.RefreshPortsCommand.Execute(null);
            Program.DiagnosticLog?.Information("Serial-port refresh requested after a Windows device-change notification.");
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Tick -= DeviceRefreshTimer_Tick;
        StopMemoryMonitor();
        _processMemoryTimer.Tick -= ProcessMemoryTimer_Tick;
        _systemMemoryTimer.Tick -= SystemMemoryTimer_Tick;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= MainViewModel_PropertyChanged;
            viewModel.PluginMenuChanged -= RefreshPluginMenu;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.RestorePersistedSessionsAsync();
            ApplySidebarVisibility(viewModel.IsSidebarVisible);
            ConfigureMemoryMonitor(viewModel);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Failed to restore persisted sessions.", exception);
        }
    }

    private void MainViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSidebarVisible) && sender is MainViewModel viewModel)
        {
            ApplySidebarVisibility(viewModel.IsSidebarVisible);
        }

        if (sender is MainViewModel memoryViewModel && e.PropertyName is
            nameof(MainViewModel.ShowMemoryMonitor) or
            nameof(MainViewModel.MemoryRefreshInterval) or
            nameof(MainViewModel.SystemMemoryRefreshInterval))
        {
            ConfigureMemoryMonitor(memoryViewModel);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        if (_shortcutEngine.TryHandleKey(key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_forceExit && DataContext is MainViewModel { CloseToTaskbar: true })
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            return;
        }

        if (_shutdownCompleted)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        IsEnabled = false;
        try
        {
            if (DataContext is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
        finally
        {
            _shutdownCompleted = true;
            // The original Closing notification must return before requesting the final
            // close, otherwise WPF rejects the reentrant Close call.
            _ = Dispatcher.BeginInvoke(Close, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    internal void RequestExit()
    {
        _forceExit = true;
        Close();
    }

    internal void FocusSendEditor()
    {
        SessionWorkspace workspace = DataContext is MainViewModel { SelectedRightSession: not null, SelectedSession: null }
            ? RightWorkspace
            : LeftWorkspace;
        workspace.FocusSendEditor();
    }

    private void DuComMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: not null } button)
        {
            OpenButtonContextMenu(button);
        }
    }

    private void ToolbarMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: not null } button)
        {
            OpenButtonContextMenu(button);
        }
    }

    private void TopMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: not null } button)
        {
            OpenButtonContextMenu(button);
        }
    }

    private void OpenButtonContextMenu(System.Windows.Controls.Button button)
    {
        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void FontSizeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem { Tag: string tag } &&
            DataContext is ViewModels.MainViewModel viewModel &&
            double.TryParse(tag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out double size))
        {
            viewModel.LogFontSize = size;
        }
    }

    internal void NotifyFloatSendClosed(string portName)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.FloatSendClosedFromWindow(portName);
        }
    }

    internal void NotifyLogFilterClosed(string portName)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.LogFilterClosedFromWindow(portName);
        }
    }

    internal void ApplyReplyWindowToFloatSends(FloatSendWindow source, int milliseconds)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ApplyReplyWindowToFloatSends(source, milliseconds);
        }
    }

}
