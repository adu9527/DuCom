using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Reflection;
using DuCom.Plugin;
using DuCom.PluginHost;
using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class PluginUiRendererFormEventTests
{
    [Fact]
    public async Task CheckboxChangeSubmitsCurrentFormWithoutInitializationCommand()
    {
        TaskCompletionSource<(string Command, IReadOnlyDictionary<string, string> Values)> invoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Exception? failure = null;
        using ManualResetEventSlim rendered = new();
        Thread thread = new(() =>
        {
            try
            {
                Application app = Application.Current ?? new Application();
                PluginCommandRouter router = new((command, values) =>
                {
                    Interlocked.Increment(ref calls);
                    invoked.TrySetResult((command, values));
                    return Task.FromResult(true);
                });
                FrameworkElement root = PluginUiRenderer.Render(
                [
                    new UiPanelNode
                    {
                        Direction = UiDirection.Horizontal,
                        ItemWidth = 300,
                        Children =
                        [
                            new UiPanelNode { Width = 300, Children = [new UiProgressNode { Width = 260 }] },
                            new UiButtonNode { CommandId = "stop", Text = "Stop", IsEnabled = false },
                        ],
                    },
                    new UiTextNode { FieldId = "name", Text = "device", Width = 180 },
                    new UiCheckBoxNode { FieldId = "enabled", Label = "Enabled", IsChecked = false, CommandId = "form-changed", SubmitOnChange = true },
                ], router);
                root.Measure(new Size(800, 600));
                root.Arrange(new Rect(0, 0, 800, 600));
                Assert.Equal(0, Volatile.Read(ref calls));
                Assert.Equal(300, Find<WrapPanel>(root).ItemWidth);
                Assert.Equal(260, Find<ProgressBar>(root).Width);
                Assert.False(Find<Button>(root).IsEnabled);
                Find<CheckBox>(root).IsChecked = true;
                rendered.Set();
                _ = app;
            }
            catch (Exception exception)
            {
                failure = exception;
                rendered.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(rendered.Wait(TimeSpan.FromSeconds(5)));
        thread.Join();
        if (failure is not null) throw failure;

        (string command, IReadOnlyDictionary<string, string> values) = await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("form-changed", command);
        Assert.Equal("true", values["enabled"]);
        Assert.Equal("device", values["name"]);
                Assert.Equal(1, calls);
    }

    [Fact]
    public void NoWrapCompactPanelBuildsHorizontalCenteredStackAndCompactProgress()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                _ = Application.Current ?? new Application();
                FrameworkElement root = PluginUiRenderer.Render(
                    [new UiPanelNode { Direction = UiDirection.Horizontal, Wrap = false, Compact = true, VerticalCenter = true, Children = [new UiProgressNode { Width = 220, Height = 14 }, new UiLabelNode { Text = "就绪", VerticalCenter = true }] }],
                    new PluginCommandRouter((_, _) => Task.FromResult(true)));
                StackPanel row = FindAll<StackPanel>(root).Single(panel => panel.Orientation == Orientation.Horizontal);
                Assert.Equal(VerticalAlignment.Center, row.VerticalAlignment);
                Assert.Equal(new Thickness(0), row.Margin);
                Assert.Equal(14, Find<ProgressBar>(row).Height);
                Assert.Equal(new Thickness(1), Find<ProgressBar>(row).BorderThickness);
                Assert.NotEqual(DependencyProperty.UnsetValue, Find<ProgressBar>(row).ReadLocalValue(Control.BackgroundProperty));
                Assert.NotEqual(DependencyProperty.UnsetValue, Find<ProgressBar>(row).ReadLocalValue(Control.BorderBrushProperty));
                Assert.Equal(VerticalAlignment.Center, FindText(row, "就绪").VerticalAlignment);
                Assert.Empty(FindAll<WrapPanel>(row));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void CalibrationProgressUsesAccentBrush()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                Application app = Application.Current ?? new Application();
                app.Resources["Brush.Accent"] = Brushes.DodgerBlue;
                app.Resources["Brush.PanelRaised"] = Brushes.Transparent;
                app.Resources["Brush.PanelBorder"] = Brushes.Transparent;
                FrameworkElement root = PluginUiRenderer.Render(
                    [new UiProgressNode { Percent = 80, State = "calibrating" }],
                    new PluginCommandRouter((_, _) => Task.FromResult(true)));
                ProgressBar progress = Find<ProgressBar>(root);
                Assert.NotEqual(DependencyProperty.UnsetValue, progress.ReadLocalValue(Control.ForegroundProperty));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void ToolWindowAppliesPageDimensionsAndDispatcherUpdateKeepsMetadata()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                Application app = Application.Current ?? new Application();
                app.Resources["Brush.PanelRaised"] = Brushes.Transparent;
                app.Resources["Brush.PanelBorder"] = Brushes.Transparent;
                app.Resources["LogPackage.Cancel"] = "Close";
                app.Resources["Style.FloatToolbarToggle"] = new Style(typeof(System.Windows.Controls.Primitives.ToggleButton));
                ToolPageContribution page = new() { ContributionId = "wide", Title = "Wide", PreferredWidth = 1100, PreferredHeight = 700, MinWidth = 900, Nodes = [new UiLabelNode { Text = "before" }] };
                PluginToolWindow window = new("plugin.test", page, "Test", (_, _) => Task.FromResult(true));
                Assert.Equal(1100, window.Width);
                Assert.Equal(700, window.Height);
                Assert.Equal(900, window.MinWidth);
                window.Close();

                PluginUiDispatcher dispatcher = new();
                FieldInfo pagesField = typeof(PluginUiDispatcher).GetField("_toolPages", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Dictionary<(string PluginId, string ContributionId), ToolPageContribution> pages = Assert.IsType<Dictionary<(string PluginId, string ContributionId), ToolPageContribution>>(pagesField.GetValue(dispatcher));
                pages[("plugin.test", "wide")] = page;
                dispatcher.UpdateToolPage("plugin.test", "wide", [new UiLabelNode { Text = "after" }]);
                ToolPageContribution updated = pages[("plugin.test", "wide")];
                Assert.Equal(1100, updated.PreferredWidth);
                Assert.Equal(700, updated.PreferredHeight);
                Assert.Equal(900, updated.MinWidth);
                Assert.Equal("after", Assert.IsType<UiLabelNode>(Assert.Single(updated.Nodes)).Text);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void TextPlaceholderIsVisibleOnlyWhileInputIsEmpty()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                _ = Application.Current ?? new Application();
                FrameworkElement root = PluginUiRenderer.Render(
                    [new UiTextNode { FieldId = "segment", Placeholder = "12", Width = 32 }],
                    new PluginCommandRouter((_, _) => Task.FromResult(true)));
                TextBox input = Find<TextBox>(root);
                TextBlock placeholder = FindText(root, "12");
                Assert.Equal(Visibility.Visible, placeholder.Visibility);
                input.Text = "AB";
                Assert.Equal(Visibility.Collapsed, placeholder.Visibility);
                input.Clear();
                Assert.Equal(Visibility.Visible, placeholder.Visibility);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try { return Find<T>(VisualTreeHelper.GetChild(root, index)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Control {typeof(T).Name} was not found.");
    }

    private static TextBlock FindText(DependencyObject root, string text)
    {
        if (root is TextBlock match && match.Text == text) return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try { return FindText(VisualTreeHelper.GetChild(root, index), text); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"TextBlock '{text}' was not found.");
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (T child in FindAll<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
