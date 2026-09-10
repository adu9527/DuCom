using System.Windows;
using System.Windows.Controls;
using DuCom.Plugin;

namespace DuCom.Services.Plugins;

public static partial class PluginUiRenderer
{
    private static FrameworkElement RenderLogPackage(IReadOnlyList<UiNode> nodes, PluginCommandRouter commands)
    {
        List<UiNode> flat = [];
        Flatten(nodes, flat);
        Dictionary<string, UiTextNode> fields = flat.OfType<UiTextNode>().ToDictionary(node => node.FieldId, StringComparer.Ordinal);
        Dictionary<string, UiButtonNode> buttons = flat.OfType<UiButtonNode>().ToDictionary(node => node.CommandId, StringComparer.Ordinal);
        UiCheckBoxNode? followLogDirectory = flat.OfType<UiCheckBoxNode>().FirstOrDefault(node => node.FieldId == "followLogDirectory");
        UiProgressNode? progress = flat.OfType<UiProgressNode>().LastOrDefault();
        UiLabelNode? status = flat.OfType<UiLabelNode>().LastOrDefault(label => label.Style == UiTextStyle.Caption);

        StackPanel root = new() { MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Center };
        Grid columns = new();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        Border information = Card(columns);
        root.Children.Add(information);

        StackPanel basic = new();
        basic.Children.Add(Section("LogPackage.BasicInfo"));
        TextBox clock = CreateLiveClock(fields.GetValueOrDefault("currentTime"));
        basic.Children.Add(new TextBlock
        {
            Text = Application.Current.TryFindResource("LogPackage.CurrentTime") as string ?? "当前时间（毫秒）",
            Style = Application.Current.TryFindResource("Style.FieldLabel") as Style,
            Margin = new Thickness(0, 10, 0, 4),
        });
        basic.Children.Add(clock);
        AddField(basic, "LogPackage.Project", fields.GetValueOrDefault("projectName"));
        AddField(basic, "LogPackage.Tester", fields.GetValueOrDefault("tester"));
        AddField(basic, "LogPackage.DeviceSoftwareVersion", fields.GetValueOrDefault("deviceSoftwareVersion"));
        AddField(basic, "LogPackage.ReproductionProbability", fields.GetValueOrDefault("reproductionProbability"));
        columns.Children.Add(basic);

        StackPanel issue = new();
        Grid.SetColumn(issue, 2);
        issue.Children.Add(Section("LogPackage.ReproductionInfo"));
        issue.Children.Add(new TextBlock
        {
            Text = Application.Current.TryFindResource("LogPackage.ReproductionTime") as string ?? "复现时间",
            Style = Application.Current.TryFindResource("Style.FieldLabel") as Style,
            Margin = new Thickness(0, 10, 0, 4),
        });
        StackPanel reproductionRow = new() { Orientation = Orientation.Horizontal };
        FrameworkElement reproductionInput = fields.TryGetValue("reproductionTime", out UiTextNode? reproduction)
            ? RenderNode(reproduction, commands)
            : new TextBox();
        reproductionRow.Children.Add(reproductionInput);
        System.Windows.Controls.Button copyTime = new()
        {
            Content = Application.Current.TryFindResource("LogPackage.CopyTime") as string ?? "复制时间",
            Margin = new Thickness(10, 0, 0, 0),
            Padding = new Thickness(14, 4, 14, 4),
        };
        copyTime.Click += (_, _) =>
        {
            if (reproductionInput is TextBox target)
            {
                target.Text = clock.Text;
            }
        };
        reproductionRow.Children.Add(copyTime);
        issue.Children.Add(reproductionRow);
        AddField(issue, "LogPackage.ProblemTitle", fields.GetValueOrDefault("title"));

        issue.Children.Add(new TextBlock
        {
            Text = Application.Current.TryFindResource("LogPackage.OutputDirectory") as string ?? "输出目录",
            Style = Application.Current.TryFindResource("Style.FieldLabel") as Style,
            Margin = new Thickness(0, 10, 0, 4),
        });
        StackPanel outputRow = new() { Orientation = Orientation.Horizontal };
        FrameworkElement outputInput = fields.TryGetValue("outputDirectory", out UiTextNode? output)
            ? RenderNode(output, commands)
            : new TextBox { IsReadOnly = true };
        outputInput.SetValue(FrameworkElement.MinWidthProperty, 160d);
        outputRow.Children.Add(outputInput);
        if (buttons.TryGetValue("browse-output", out UiButtonNode? browse))
        {
            FrameworkElement browseButton = RenderNode(browse, commands);
            browseButton.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
            outputRow.Children.Add(browseButton);
        }

        issue.Children.Add(outputRow);
        if (followLogDirectory is not null)
        {
            CheckBox follow = (CheckBox)RenderNode(followLogDirectory, commands);
            follow.Margin = new Thickness(0, 10, 0, 0);
            follow.Click += (_, _) => commands.InvokeCommand("toggle-follow", submitForm: true);
            issue.Children.Add(follow);
        }

        columns.Children.Add(issue);

        StackPanel selection = new();
        selection.Children.Add(Section("LogPackage.LogSelection"));
        selection.Children.Add(Muted("LogPackage.LogSelectionHint"));
        foreach (UiCheckBoxNode session in flat.OfType<UiCheckBoxNode>().Where(node => node.FieldId.StartsWith("selection:", StringComparison.Ordinal)))
        {
            Grid row = new() { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            CheckBox check = (CheckBox)RenderNode(session, commands);
            check.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(check);
            if (fields.TryGetValue("device:" + session.Label, out UiTextNode? device))
            {
                FrameworkElement input = RenderNode(device, commands);
                input.Margin = new Thickness(12, 0, 0, 0);
                input.SetBinding(UIElement.IsEnabledProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked)) { Source = check });
                Grid.SetColumn(input, 1);
                row.Children.Add(input);
            }
            selection.Children.Add(row);
        }
        if (buttons.TryGetValue("refresh-sessions", out UiButtonNode? refresh)) selection.Children.Add(RenderNode(refresh, commands));
        root.Children.Add(Card(selection, new Thickness(0, 12, 0, 0)));

        StackPanel descriptions = new();
        descriptions.Children.Add(Section("LogPackage.Description"));
        AddField(descriptions, "LogPackage.ProblemDescription", fields.GetValueOrDefault("problemDescription"));
        AddField(descriptions, "LogPackage.ReproductionSteps", fields.GetValueOrDefault("reproductionSteps"));
        AddField(descriptions, "LogPackage.Notes", fields.GetValueOrDefault("notes"));
        root.Children.Add(Card(descriptions, new Thickness(0, 12, 0, 0)));

        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (string id in new[] { "save-form", "cancel", "pack" })
        {
            if (buttons.TryGetValue(id, out UiButtonNode? button)) actions.Children.Add(RenderNode(button, commands));
        }
        root.Children.Add(actions);
        if (progress is not null) root.Children.Add(RenderNode(progress, commands));
        else if (status is not null) root.Children.Add(RenderNode(status, commands));

        commands.FormRoot = root;
        ApplyTheme(root);
        clock.SetResourceReference(Control.ForegroundProperty, "Brush.TextSecondary");
        if (outputInput is Control outputControl)
        {
            outputControl.SetResourceReference(Control.ForegroundProperty, "Brush.TextSecondary");
        }

        return root;
    }

    /// <summary>The legacy read-only millisecond clock, ticking on the UI thread while the page is open.</summary>
    private static TextBox CreateLiveClock(UiTextNode? node)
    {
        TextBox clock = new()
        {
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Tag = node?.FieldId ?? "currentTime",
        };
        System.Windows.Threading.DispatcherTimer timer = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(31) };
        void Update() => clock.Text = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        timer.Tick += (_, _) => Update();
        clock.Loaded += (_, _) =>
        {
            Update();
            timer.Start();
        };
        clock.Unloaded += (_, _) => timer.Stop();
        Update();
        return clock;
    }
}
