using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DuCom.Behaviors;

/// <summary>Let a nested, exhausted scroll viewer pass wheel input to its scrollable parent.</summary>
public static class NestedScrollWheelBehavior
{
    public static void Attach(FrameworkElement surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        surface.PreviewMouseWheel -= OnWheel;
        surface.PreviewMouseWheel += OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not FrameworkElement surface) return;
        DependencyObject? source = e.OriginalSource as DependencyObject;
        ScrollViewer? inner = null;
        for (DependencyObject? node = source; node is not null; node = Parent(node))
        {
            // Preserve dropdown selection and an open popup's own scroll/capture semantics.
            if (node is ComboBox or ComboBoxItem) return;
            if (node is ScrollViewer viewer) { inner = viewer; break; }
            if (ReferenceEquals(node, surface)) break;
        }
        // A DataGrid cell may be the event source before its ScrollViewer is reached.
        if (inner is null)
            inner = FindViewer(surface);
        if (inner is null || CanScroll(inner, e.Delta)) return;
        for (DependencyObject? node = Parent(inner); node is not null; node = Parent(node))
        {
            if (node is not ScrollViewer outer || !CanScroll(outer, e.Delta)) continue;
            // Raise a bubbling event directly on the parent; no recursive preview route.
            MouseWheelEventArgs forwarded = new(e.MouseDevice, e.Timestamp, e.Delta)
            { RoutedEvent = Mouse.MouseWheelEvent, Source = outer };
            outer.RaiseEvent(forwarded);
            e.Handled = forwarded.Handled;
            return;
        }
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        delta < 0 ? viewer.VerticalOffset < viewer.ScrollableHeight - 0.01 : viewer.VerticalOffset > 0.01;

    private static DependencyObject? Parent(DependencyObject node) => node is Visual
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static ScrollViewer? FindViewer(DependencyObject node)
    {
        if (node is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (FindViewer(VisualTreeHelper.GetChild(node, i)) is { } child) return child;
        return null;
    }
}
