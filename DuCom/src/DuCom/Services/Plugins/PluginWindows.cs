using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DuCom.Plugin;
using DuCom.PluginHost;
using Wpf.Ui.Controls;

namespace DuCom.Services.Plugins;

public partial class PluginToolWindow : FluentWindow
{
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<bool>> _commandInvoker;
    private PluginCommandRouter? _router;

    public PluginToolWindow(string pluginId, string contributionId, string title, Func<string, IReadOnlyDictionary<string, string>, Task<bool>> commandInvoker)
    {
        _commandInvoker = commandInvoker;
        PluginId = pluginId;
        ContributionId = contributionId;
        Title = title;
        Width = pluginId == "com.ducom.log-package" ? 1080 : 820;
        Height = pluginId == "com.ducom.log-package" ? 900 : 720;
        MinWidth = pluginId == "com.ducom.log-package" ? 760 : 640;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;

        SetResourceReference(ForegroundProperty, "Brush.TextPrimary");
        SetResourceReference(BackgroundProperty, "Brush.ShellSurface");
        Grid shell = new();
        shell.SetResourceReference(Panel.BackgroundProperty, "Brush.ShellSurface");
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        TitleBar titleBar = new() { Title = title, ShowMaximize = true, ShowMinimize = true };
        shell.Children.Add(titleBar);
        ScrollViewer viewer = new()
        {
            Margin = new Thickness(24, 16, 24, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(viewer, 1);
        shell.Children.Add(viewer);
        // Pin the page width to the viewport (clamped by its MaxWidth): otherwise the
        // centered panel resizes with its longest wrapped text and cards visibly jump.
        viewer.SizeChanged += (_, args) => ApplyViewportWidth(viewer, args.NewSize.Width);
        Border footer = new()
        {
            Padding = new Thickness(16, 10, 16, 10),
            Background = FindResource("Brush.PanelRaised") as System.Windows.Media.Brush,
            BorderBrush = FindResource("Brush.PanelBorder") as System.Windows.Media.Brush,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        System.Windows.Controls.Button close = new()
        {
            Content = FindResource("LogPackage.Cancel") as string ?? "关闭",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        close.Click += (_, _) => Close();
        PluginUiRenderer.ApplyTheme(footer);
        PluginUiRenderer.ApplyTheme(close);
        footer.Child = close;
        Grid.SetRow(footer, 2);
        shell.Children.Add(footer);
        Content = shell;
    }

    public string PluginId { get; }

    public string ContributionId { get; }

    public void SetContent(IReadOnlyList<UiNode>? nodes)
    {
        _router ??= new PluginCommandRouter(async (commandId, values) => await _commandInvoker(commandId, values));
        ScrollViewer viewer = (ScrollViewer)((Grid)Content).Children[1];
        IReadOnlyDictionary<string, string> pendingValues = viewer.Content is DependencyObject existing
            ? PluginUiRenderer.CollectFormValues(existing)
            : new Dictionary<string, string>();
        IReadOnlyList<UiNode>? displayNodes = nodes is null ? null : PreserveFormValues(nodes, pendingValues);
        viewer.Content = nodes is null || nodes.Count == 0
            ? new System.Windows.Controls.TextBlock { Text = TryFindResource("Plugins.ToolPage.Empty") as string ?? "No content", Margin = new Thickness(12) }
            : PluginUiRenderer.Render(displayNodes!, _router, PluginId);
        if (viewer.Content is FrameworkElement content)
        {
            PluginUiRenderer.ApplyTheme(content);
            ApplyViewportWidth(viewer, viewer.ViewportWidth);
        }
    }

    private static void ApplyViewportWidth(ScrollViewer viewer, double viewportWidth)
    {
        if (viewer.Content is not FrameworkElement content || double.IsNaN(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

        double max = double.IsNaN(content.MaxWidth) || content.MaxWidth == double.PositiveInfinity ? double.PositiveInfinity : content.MaxWidth;
        content.Width = Math.Min(viewportWidth, max);
    }

    private static IReadOnlyList<UiNode> PreserveFormValues(IReadOnlyList<UiNode> nodes, IReadOnlyDictionary<string, string> values) =>
        [.. nodes.Select(node => node switch
        {
            UiPanelNode panel => panel with { Children = PreserveFormValues(panel.Children, values) },
            UiTextNode text when !text.ReadOnly && values.TryGetValue(text.FieldId, out string? value) => text with { Text = value },
            UiCheckBoxNode check when values.TryGetValue(check.FieldId, out string? value) => check with { IsChecked = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) },
            UiSelectNode select when values.TryGetValue(select.FieldId, out string? value) => select with { Selected = value },
            UiSliderNode slider when values.TryGetValue(slider.FieldId, out string? value)
                && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double parsed) => slider with { Value = parsed },
            _ => node,
        })];

    public void Refresh(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes)
    {
        if (string.Equals(PluginId, pluginId, StringComparison.Ordinal)
            && string.Equals(ContributionId, contributionId, StringComparison.Ordinal))
        {
            SetContent(nodes);
        }
    }
}

public partial class PluginFaultNoticeWindow : FluentWindow
{
    public PluginFaultNoticeWindow(ObservableCollection<HostFaultNotice> notices)
    {
        Title = TryFindResource("Plugins.FaultNotice.Title") as string ?? "DuCom plugin notice";
        Width = 460;
        Height = 320;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Right - 480;
        Top = SystemParameters.WorkArea.Bottom - 340;

        ListBox list = new()
        {
            ItemsSource = notices,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        list.ItemTemplate = CreateNoticeTemplate();
        SetResourceReference(ForegroundProperty, "Brush.TextPrimary");
        SetResourceReference(BackgroundProperty, "Brush.ShellSurface");
        PluginUiRenderer.ApplyTheme(list);
        Content = list;
    }

    private System.Windows.DataTemplate CreateNoticeTemplate()
    {
        FrameworkElementFactory panel = new(typeof(StackPanel));
        panel.SetValue(StackPanel.MarginProperty, new Thickness(8));

        FrameworkElementFactory title = new(typeof(System.Windows.Controls.TextBlock));
        title.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("PluginName"));
        title.SetValue(System.Windows.Controls.TextBlock.FontWeightProperty, FontWeights.Bold);
        title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.TextPrimary");
        title.SetValue(System.Windows.Controls.TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
        panel.AppendChild(title);

        FrameworkElementFactory reason = new(typeof(System.Windows.Controls.TextBlock));
        reason.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Reason"));
        reason.SetValue(System.Windows.Controls.TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        reason.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.TextPrimary");
        panel.AppendChild(reason);

        FrameworkElementFactory meta = new(typeof(System.Windows.Controls.TextBlock));
        meta.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Version"));
        meta.SetValue(System.Windows.Controls.TextBlock.FontSizeProperty, 11d);
        meta.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.TextSecondary");
        meta.SetValue(System.Windows.Controls.TextBlock.OpacityProperty, 0.7d);
        panel.AppendChild(meta);

        System.Windows.DataTemplate template = new()
        {
            VisualTree = panel,
        };
        return template;
    }
}
