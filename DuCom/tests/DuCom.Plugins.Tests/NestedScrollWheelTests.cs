using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DuCom.Behaviors;
using DuCom.Plugins.Production;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class NestedScrollWheelTests
{
    [Fact]
    public void ActualDetailFieldTablePassesWheelWhenItCannotScroll() => WpfTestDispatcher.Run(() =>
    {
        using PluginUiTestScope scope = new();
        ProductionCommandEditor window = new(ProductionProfile.CreateExample().Commands[0], new());
        scope.Track(window);
        window.Height = 600;
        window.Show(); Settle(window);
        ScrollViewer outer = Assert.IsType<ScrollViewer>(((Grid)window.Content).Children[0]);
        DataGrid table = Descendants<DataGrid>(window).Single();
        ScrollViewer inner = Descendants<ScrollViewer>(table).First();
        Assert.True(outer.ScrollableHeight > 0);
        Assert.Equal(0, inner.ScrollableHeight);
        outer.ScrollToTop(); Settle(window);
        Wheel(inner, -120); Settle(window);
        Assert.True(outer.VerticalOffset > 0, "A one-row field grid must not trap the page wheel.");
        Wheel(inner, 120); Settle(window);
        Assert.Equal(0, outer.VerticalOffset);
    });

    [Fact]
    public void LongTableKeepsOwnScrollingAndTransfersOnlyAtItsBoundary() => WpfTestDispatcher.Run(() =>
    {
        StackPanel rows = new();
        for (int i = 0; i < 30; i++) rows.Children.Add(new TextBlock { Text = "row " + i, Height = 30 });
        ScrollViewer inner = new() { Content = rows, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        StackPanel page = new(); page.Children.Add(inner); page.Children.Add(new Border { Height = 1000 });
        ScrollViewer outer = new() { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Window window = new() { Width = 400, Height = 400, Content = outer };
        NestedScrollWheelBehavior.Attach(inner);
        try
        {
            window.Show(); Settle(window);
            Wheel(inner, -120); Settle(window);
            Assert.True(inner.VerticalOffset > 0);
            Assert.Equal(0, outer.VerticalOffset);
            inner.ScrollToBottom(); Settle(window);
            Wheel(inner, -120); Settle(window);
            Assert.True(outer.VerticalOffset > 0);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ComboBoxKeepsItsWheelAndFocusSemantics() => WpfTestDispatcher.Run(() =>
    {
        ComboBox combo = new() { ItemsSource = new[] { "A", "B" }, SelectedIndex = 0, Height = 40 };
        ScrollViewer inner = new() { Content = combo, Height = 80 };
        StackPanel page = new(); page.Children.Add(inner); page.Children.Add(new Border { Height = 1000 });
        ScrollViewer outer = new() { Content = page };
        Window window = new() { Width = 400, Height = 400, Content = outer };
        NestedScrollWheelBehavior.Attach(inner);
        try
        {
            window.Show(); Settle(window);
            combo.Focus();
            Wheel(combo, -120); Settle(window);
            Assert.Equal(0, outer.VerticalOffset);
        }
        finally { window.Close(); }
    });

    private static void Wheel(UIElement target, int delta)
    {
        MouseWheelEventArgs args = new(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = target };
        target.RaiseEvent(args);
        if (!args.Handled) target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = Mouse.MouseWheelEvent, Source = target });
    }

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        DispatcherFrame frame = new();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (T child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
