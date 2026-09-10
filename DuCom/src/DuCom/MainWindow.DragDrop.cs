using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using DuCom.ViewModels;

namespace DuCom;

public partial class MainWindow
{
    private Point _dragStart;

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
}
