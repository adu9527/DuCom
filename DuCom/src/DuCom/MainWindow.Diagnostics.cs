using System.Windows;
using DuCom.ViewModels;

namespace DuCom;

public partial class MainWindow
{
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
}
