using System.ComponentModel;
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
    private Point _dragStart;
    private readonly ShortcutEngine _shortcutEngine;
    private bool _splitLayoutInitialized;
    private readonly DispatcherTimer _deviceRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private HwndSource? _windowSource;
    private GridLength _visibleConnectionColumnWidth = new(220);

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
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= MainViewModel_PropertyChanged;
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
    }

    private void ApplySidebarVisibility(bool visible)
    {
        if (!visible && ConnectionColumn.Width.Value > 0)
        {
            _visibleConnectionColumnWidth = ConnectionColumn.Width;
        }

        ConnectionColumn.Width = visible
            ? _visibleConnectionColumnWidth.Value > 0 ? _visibleConnectionColumnWidth : new GridLength(220)
            : new GridLength(0);
        ConnectionSplitterColumn.Width = visible ? new GridLength(4) : new GridLength(0);
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

    internal string ValidateShellLayouts()
    {
        (double Width, double Height)[] sizes =
        [
            (960, 640),
            (1366, 768),
            (1920, 1080),
        ];

        List<string> results = [];
        foreach ((double width, double height) in sizes)
        {
            Width = width;
            Height = height;
            UpdateLayout();

            if (ActualWidth < width || ActualHeight < height)
            {
                throw new InvalidOperationException(
                    $"Window did not reach requested shell size {width}x{height}. Actual={ActualWidth}x{ActualHeight}.");
            }

            if (LogWorkspace.ActualWidth <= 0 || LogWorkspace.ActualHeight <= 0 || !LogWorkspace.IsVisible)
            {
                throw new InvalidOperationException($"Log workspace is not visible at {width}x{height}.");
            }

            ValidateVisibleButton(FileMenuButton, "File menu", width, height);
            ValidateVisibleButton(ViewMenuButton, "View menu", width, height);
            ValidateVisibleButton(ToolsMenuButton, "Tools menu", width, height);
            ValidateVisibleButton(AboutMenuButton, "About menu", width, height);
            ValidateVisibleButton(PortViewButton, "Port view", width, height);
            ValidateVisibleButton(PortSortButton, "Port sort", width, height);

            results.Add($"{width}x{height}:log={LogWorkspace.ActualWidth:0.#}x{LogWorkspace.ActualHeight:0.#}");
        }

        if (DataContext is MainViewModel viewModel)
        {
            bool initialVisibility = viewModel.IsSidebarVisible;
            if (initialVisibility)
            {
                viewModel.ToggleSidebarCommand.Execute(null);
            }

            UpdateLayout();
            if (ConnectionColumn.ActualWidth > 0.1d || ConnectionSplitterColumn.ActualWidth > 0.1d)
            {
                throw new InvalidOperationException(
                    $"Sidebar command did not collapse its columns. Sidebar={ConnectionColumn.ActualWidth:0.#}; Splitter={ConnectionSplitterColumn.ActualWidth:0.#}.");
            }
            ToolsMenuButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (ToolsMenuButton.ContextMenu?.IsOpen != true)
            {
                throw new InvalidOperationException("Tools menu did not open while the sidebar was hidden.");
            }

            ToolsMenuButton.ContextMenu.IsOpen = false;
            if (viewModel.IsSidebarVisible != initialVisibility)
            {
                viewModel.ToggleSidebarCommand.Execute(null);
                UpdateLayout();
            }

            DuComMenuButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (DuComMenuButton.ContextMenu?.IsOpen != true)
            {
                throw new InvalidOperationException("DuCom menu did not open.");
            }

            DuComMenuButton.ContextMenu.IsOpen = false;
            if (viewModel.IsSidebarVisible != initialVisibility)
            {
                throw new InvalidOperationException("Sidebar command did not restore its initial visibility.");
            }
        }

        return string.Join("; ", results);
    }

    private static void ValidateVisibleButton(FrameworkElement button, string name, double width, double height)
    {
        if (!button.IsVisible || button.ActualWidth < 24 || button.ActualHeight < 24)
        {
            throw new InvalidOperationException($"{name} is clipped or hidden at {width}x{height}. Actual={button.ActualWidth:0.#}x{button.ActualHeight:0.#}.");
        }
    }

    private void DragSource_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _dragStart = e.GetPosition(this);

    private void PortList_PreviewMouseMove(object sender, MouseEventArgs e) =>
        BeginPortDrag(e, FindDataContext<PortItemViewModel>(e.OriginalSource as DependencyObject)?.PortName);

    private void SessionTabs_PreviewMouseMove(object sender, MouseEventArgs e) =>
        BeginPortDrag(e, FindDataContext<SessionViewModel>(e.OriginalSource as DependencyObject)?.PortName);

    private async void SessionTabs_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("DuCom.PortName") is not string portName ||
            sender is not ListBox tabs ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        bool rightPane = ReferenceEquals(tabs, RightSessionTabs);

        // Dragging a tab onto the right tab strip but the session is not yet in the
        // right pane means the user wants to move it across panes — add it to the split
        // instead of trying (and failing) to reorder it within the right collection.
        if (rightPane && viewModel.RightSessions.All(item =>
                !string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                await viewModel.AssignRightPaneAsync(portName);
            }
            catch (Exception exception)
            {
                Program.DiagnosticLog?.Error($"Failed to assign right pane on tab drop. Port={portName}", exception);
            }

            e.Handled = true;
            return;
        }

        int targetIndex = tabs.Items.Count - 1;
        if (FindDataContext<SessionViewModel>(e.OriginalSource as DependencyObject) is { } target)
        {
            targetIndex = tabs.Items.IndexOf(target);
        }

        viewModel.MoveSessionTab(portName, Math.Max(0, targetIndex), rightPane);
        e.Handled = true;
    }

    private void BeginPortDrag(MouseEventArgs e, string? portName)
    {
        if (e.LeftButton != MouseButtonState.Pressed || string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DataObject data = new("DuCom.PortName", portName);
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
    }

    private void LogWorkspace_DragOver(object sender, DragEventArgs e)
    {
        bool canDrop = e.Data.GetDataPresent("DuCom.PortName") && e.GetPosition(LogWorkspace).X >= LogWorkspace.ActualWidth / 2;
        e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void LogWorkspace_DragLeave(object sender, DragEventArgs e)
    {
    }

    private async void LogWorkspace_Drop(object sender, DragEventArgs e)
    {
        if (e.GetPosition(LogWorkspace).X < LogWorkspace.ActualWidth / 2 ||
            e.Data.GetData("DuCom.PortName") is not string portName ||
            DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            await viewModel.AssignRightPaneAsync(portName);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error($"Failed to assign right pane. Port={portName}", exception);
        }
        e.Handled = true;
    }

    private static T? FindDataContext<T>(DependencyObject? source) where T : class
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is T match)
            {
                return match;
            }

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SplitWorkspaceGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_splitLayoutInitialized && DataContext is MainViewModel viewModel)
        {
            _splitLayoutInitialized = true;
            ApplySplitLayout(viewModel);
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(MainViewModel.SplitOrientation) or nameof(MainViewModel.IsSplitView))
                {
                    ApplySplitLayout(viewModel);
                }
            };
        }
    }

    private void ApplySplitLayout(MainViewModel viewModel)
    {
        double ratio = Math.Clamp(viewModel.SplitterRatio, 0.2d, 0.8d);
        bool split = viewModel.IsSplitView;
        if (viewModel.SplitOrientation == SplitLayoutOrientation.Horizontal)
        {
            Grid.SetRow(SessionGridSplitter, 1);
            Grid.SetColumn(SessionGridSplitter, 0);
            Grid.SetColumnSpan(SessionGridSplitter, 3);
            SessionGridSplitter.Width = double.NaN;
            SessionGridSplitter.Height = 6;
            PrimarySplitColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitGapColumn.Width = new GridLength(0);
            SecondarySplitColumn.Width = new GridLength(0);
            PrimarySplitRow.Height = new GridLength(split ? ratio : 1, GridUnitType.Star);
            SplitGapRow.Height = new GridLength(split ? 6 : 0);
            SecondarySplitRow.Height = new GridLength(split ? 1 - ratio : 0, GridUnitType.Star);
        }
        else
        {
            Grid.SetRow(SessionGridSplitter, 0);
            Grid.SetColumn(SessionGridSplitter, 1);
            Grid.SetColumnSpan(SessionGridSplitter, 1);
            SessionGridSplitter.Width = 6;
            SessionGridSplitter.Height = double.NaN;
            PrimarySplitRow.Height = new GridLength(1, GridUnitType.Star);
            SplitGapRow.Height = new GridLength(0);
            SecondarySplitRow.Height = new GridLength(0);
            PrimarySplitColumn.Width = new GridLength(split ? ratio : 1, GridUnitType.Star);
            SplitGapColumn.Width = new GridLength(split ? 6 : 0);
            SecondarySplitColumn.Width = new GridLength(split ? 1 - ratio : 0, GridUnitType.Star);
        }
    }

    private void SessionGridSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        double primary = viewModel.SplitOrientation == SplitLayoutOrientation.Horizontal
            ? PrimarySplitRow.ActualHeight
            : PrimarySplitColumn.ActualWidth;
        double secondary = viewModel.SplitOrientation == SplitLayoutOrientation.Horizontal
            ? SecondarySplitRow.ActualHeight
            : SecondarySplitColumn.ActualWidth;
        if (primary + secondary > 0)
        {
            viewModel.SplitterRatio = Math.Clamp(primary / (primary + secondary), 0.2d, 0.8d);
        }
    }

}
