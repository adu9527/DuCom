using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DuCom.ViewModels;

namespace DuCom;

public partial class MainWindow
{
    private bool _splitLayoutInitialized;
    private GridLength _visibleConnectionColumnWidth = new(220);

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
