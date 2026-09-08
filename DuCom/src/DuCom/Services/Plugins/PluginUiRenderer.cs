using System.Windows;
using System.Windows.Controls;
using DuCom.Plugin;

namespace DuCom.Services.Plugins;

/// <summary>
/// Renders the bounded declarative UI tree contributed by plugins. The host owns every
/// control; plugin code never provides XAML, converters, or assemblies. Node counts, depth,
/// list lengths, and text lengths were validated by UiContributionValidator before this
/// renderer sees them.
/// </summary>
public static class PluginUiRenderer
{
    public static FrameworkElement Render(IReadOnlyList<UiNode> nodes, PluginCommandRouter commands, string? pluginId = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(commands);
        if (string.Equals(pluginId, "com.ducom.log-package", StringComparison.Ordinal))
        {
            return RenderLogPackage(nodes, commands);
        }

        if (string.Equals(pluginId, "com.ducom.background-image", StringComparison.Ordinal))
        {
            return RenderBackgroundImage(nodes, commands);
        }

        Panel root = MakePanel(UiDirection.Vertical);
        foreach (FrameworkElement element in nodes.Select(node => RenderNode(node, commands)))
        {
            root.Children.Add(element);
        }

        commands.FormRoot = root;
        ApplyTheme(root);
        return root;
    }

    private static FrameworkElement RenderBackgroundImage(IReadOnlyList<UiNode> nodes, PluginCommandRouter commands)
    {
        List<UiNode> flat = [];
        Flatten(nodes, flat);
        Dictionary<string, UiTextNode> fields = flat.OfType<UiTextNode>().ToDictionary(node => node.FieldId, StringComparer.Ordinal);
        Dictionary<string, UiButtonNode> buttons = flat.OfType<UiButtonNode>().ToDictionary(node => node.CommandId, StringComparer.Ordinal);
        UiCheckBoxNode? enabled = flat.OfType<UiCheckBoxNode>().FirstOrDefault(node => node.FieldId == "enabled");
        UiSelectNode? playback = flat.OfType<UiSelectNode>().FirstOrDefault(node => node.FieldId == "playback");
        UiSliderNode? opacity = flat.OfType<UiSliderNode>().FirstOrDefault(node => node.FieldId == "opacity");
        UiLabelNode[] paths = [.. flat.OfType<UiLabelNode>().Where(node => node.Style == UiTextStyle.Normal)];

        StackPanel root = new() { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Center };
        TextBlock title = new()
        {
            Text = Application.Current.TryFindResource("Plugins.BackgroundImage.Name") as string ?? "背景图",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
        };
        root.Children.Add(title);
        root.Children.Add(Muted("Plugins.BackgroundImage.Description"));

        // Every control applies live (legacy behavior); no save button is rendered.
        StackPanel settings = new();
        settings.Children.Add(Section("Plugins.BackgroundImage.Image"));
        TextBox imageBox = new() { Text = paths.ElementAtOrDefault(0)?.Text ?? string.Empty, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        imageBox.SetResourceReference(Control.ForegroundProperty, "Brush.TextSecondary");
        settings.Children.Add(imageBox);
        StackPanel pickers = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (string command in new[] { "pick-image", "pick-folder", "next" })
        {
            if (buttons.TryGetValue(command, out UiButtonNode? button)) pickers.Children.Add(RenderNode(button, commands));
        }

        settings.Children.Add(pickers);
        settings.Children.Add(new TextBlock { Text = Application.Current.TryFindResource("Plugins.BackgroundImage.Folder") as string ?? "图片文件夹", Style = Application.Current.TryFindResource("Style.FieldLabel") as Style, Margin = new Thickness(0, 16, 0, 4) });
        TextBox folderBox = new() { Text = paths.ElementAtOrDefault(1)?.Text ?? string.Empty, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        folderBox.SetResourceReference(Control.ForegroundProperty, "Brush.TextSecondary");
        settings.Children.Add(folderBox);
        if (playback is not null)
        {
            settings.Children.Add(new TextBlock { Text = Application.Current.TryFindResource("Plugins.BackgroundImage.PlaybackMode") as string ?? "播放模式", Style = Application.Current.TryFindResource("Style.FieldLabel") as Style, Margin = new Thickness(0, 16, 0, 4) });
            FrameworkElement playbackControl = RenderNode(playback, commands);
            if (playbackControl is System.Windows.Controls.Primitives.Selector selector)
            {
                selector.SelectionChanged += (_, _) => commands.InvokeCommand("apply", submitForm: true);
            }

            settings.Children.Add(playbackControl);
        }

        if (opacity is not null)
        {
            settings.Children.Add(new TextBlock { Text = Application.Current.TryFindResource("Plugins.BackgroundImage.Opacity") as string ?? "图片透明度", Style = Application.Current.TryFindResource("Style.FieldLabel") as Style, Margin = new Thickness(0, 16, 0, 4) });
            settings.Children.Add(CreateLiveOpacitySlider(opacity, commands));
        }

        if (fields.TryGetValue("intervalSeconds", out UiTextNode? interval) && RenderNode(interval, commands) is Control intervalControl)
        {
            settings.Children.Add(new TextBlock { Text = Application.Current.TryFindResource("Plugins.BackgroundImage.Interval") as string ?? "定时切换间隔（秒）", Style = Application.Current.TryFindResource("Style.FieldLabel") as Style, Margin = new Thickness(0, 16, 0, 4) });
            intervalControl.LostFocus += (_, _) => commands.InvokeCommand("apply", submitForm: true);
            settings.Children.Add(intervalControl);
        }

        root.Children.Add(Card(settings, new Thickness(0, 18, 0, 0)));

        Border preview = new()
        {
            MinHeight = 220,
            Margin = new Thickness(0, 18, 0, 0),
            Background = Application.Current.TryFindResource("Brush.ShellSurface") as System.Windows.Media.Brush,
            BorderBrush = Application.Current.TryFindResource("Brush.PanelBorder") as System.Windows.Media.Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
        };
        Grid previewGrid = new();
        previewGrid.Children.Add(new TextBlock { Text = Application.Current.TryFindResource("Plugins.BackgroundImage.Preview") as string ?? "背景图片预览", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Style = Application.Current.TryFindResource("Style.MutedText") as Style });
        if (Application.Current.MainWindow?.DataContext is DuCom.ViewModels.MainViewModel main && main.PluginSystem is { } system)
        {
            Image image = new() { Stretch = System.Windows.Media.Stretch.UniformToFill };
            image.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding(nameof(BackgroundImageHostService.ImageSource)) { Source = system.Background });
            image.SetBinding(UIElement.OpacityProperty, new System.Windows.Data.Binding(nameof(BackgroundImageHostService.Opacity)) { Source = system.Background });
            previewGrid.Children.Add(image);
        }
        preview.Child = previewGrid;
        root.Children.Add(preview);

        StackPanel enableCard = new();
        if (enabled is not null)
        {
            CheckBox toggle = (CheckBox)RenderNode(enabled, commands);
            toggle.Click += (_, _) => commands.InvokeCommand("apply", submitForm: true);
            enableCard.Children.Add(toggle);
        }

        root.Children.Insert(2, Card(enableCard, new Thickness(0, 18, 0, 0)));
        commands.FormRoot = root;
        ApplyTheme(root);
        // ApplyTheme rebinds every control's foreground; re-assert the read-only gray afterwards.
        imageBox.SetResourceReference(Control.ForegroundProperty, "Brush.TextSecondary");
        folderBox.SetResourceReference(Control.ForegroundProperty, "Brush.TextSecondary");
        return root;
    }

    /// <summary>A 0-100 slider styled like the built-in feature: percentage text plus a debounced live apply.</summary>
    private static FrameworkElement CreateLiveOpacitySlider(UiSliderNode node, PluginCommandRouter commands)
    {
        StackPanel host = new();
        TextBlock percent = new()
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = Application.Current.TryFindResource("Style.MutedText") as Style,
        };
        Slider slider = new()
        {
            Minimum = node.Min,
            Maximum = node.Max,
            Value = Math.Clamp(node.Value, node.Min, node.Max),
            Tag = node.FieldId,
            IsMoveToPointEnabled = true,
        };
        if (Application.Current.TryFindResource("Style.PrecisionSlider") is Style precision)
        {
            slider.Style = precision;
        }

        System.Windows.Threading.DispatcherTimer debounce = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        void UpdatePercent() => percent.Text = $"{Math.Round(slider.Value)}{node.UnitLabel ?? "%"}";
        slider.ValueChanged += (_, _) =>
        {
            UpdatePercent();
            debounce.Stop();
            debounce.Start();
        };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            commands.InvokeCommand("apply", submitForm: true);
        };
        slider.Unloaded += (_, _) => debounce.Stop();
        UpdatePercent();
        host.Children.Add(slider);
        percent.Margin = new Thickness(0, 4, 0, 0);
        host.Children.Add(percent);
        return host;
    }

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

    internal static void ApplyTheme(FrameworkElement root)
    {
        if (root is TextBlock text)
        {
            string brush = "Brush.TextPrimary";
            if (text.Style is { } style)
            {
                if (style == Application.Current.TryFindResource("Style.MutedText") || style == Application.Current.TryFindResource("PluginUi.Caption")) brush = "Brush.TextSecondary";
                else if (style == Application.Current.TryFindResource("PluginUi.Accent")) brush = "Brush.Accent";
                else if (style == Application.Current.TryFindResource("PluginUi.Warning")) brush = "Brush.Danger";
            }
            text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        }
        if (root is Control control)
        {
            control.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
            if (control is TextBox or ComboBox or ComboBoxItem or ListBox or ListBoxItem or Button)
            {
                control.SetResourceReference(Control.BackgroundProperty, "Brush.PanelRaised");
                control.SetResourceReference(Control.BorderBrushProperty, "Brush.PanelBorder");
            }
        }
        if (root is Border border)
        {
            border.SetResourceReference(Border.BackgroundProperty, "Brush.PanelRaised");
            border.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        }
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is FrameworkElement element) ApplyTheme(element);
    }

    private static void Flatten(IEnumerable<UiNode> nodes, List<UiNode> result)
    {
        foreach (UiNode node in nodes)
        {
            if (node is UiPanelNode panel) Flatten(panel.Children, result);
            else result.Add(node);
        }
    }

    private static Border Card(UIElement child, Thickness? margin = null) => new()
    {
        Child = child,
        Margin = margin ?? new Thickness(0),
        Style = Application.Current.TryFindResource("Style.SettingsCard") as Style,
    };

    private static TextBlock Section(string resource) => new()
    {
        Text = Application.Current.TryFindResource(resource) as string ?? resource,
        Style = Application.Current.TryFindResource("Style.SectionTitle") as Style,
        Margin = new Thickness(0, 0, 0, 12),
    };

    private static TextBlock Muted(string resource) => new()
    {
        Text = Application.Current.TryFindResource(resource) as string ?? resource,
        Style = Application.Current.TryFindResource("Style.MutedText") as Style,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static void AddField(Panel panel, string labelResource, UiTextNode? field)
    {
        if (field is null) return;
        panel.Children.Add(new TextBlock
        {
            Text = Application.Current.TryFindResource(labelResource) as string ?? labelResource,
            Style = Application.Current.TryFindResource("Style.FieldLabel") as Style,
            Margin = new Thickness(0, 10, 0, 4),
        });
        panel.Children.Add(RenderNode(field, new PluginCommandRouter((_, _) => Task.FromResult(false))));
    }

    private static FrameworkElement RenderNode(UiNode node, PluginCommandRouter commands)
    {
        switch (node)
        {
            case UiPanelNode panel:
                Panel container = MakePanel(panel.Direction);
                foreach (UiNode child in panel.Children)
                {
                    container.Children.Add(RenderNode(child, commands));
                }

                return container;
            case UiLabelNode label:
                return new TextBlock
                {
                    Text = label.Text,
                    TextWrapping = label.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    Style = label.Style switch
                    {
                        UiTextStyle.Heading => (Style)Application.Current.TryFindResource("PluginUi.Heading") ?? new Style(typeof(TextBlock)),
                        UiTextStyle.Caption => (Style)Application.Current.TryFindResource("PluginUi.Caption") ?? new Style(typeof(TextBlock)),
                        UiTextStyle.Accent => (Style)Application.Current.TryFindResource("PluginUi.Accent") ?? new Style(typeof(TextBlock)),
                        UiTextStyle.Warning => (Style)Application.Current.TryFindResource("PluginUi.Warning") ?? new Style(typeof(TextBlock)),
                        _ => (Style)Application.Current.TryFindResource("PluginUi.Normal") ?? new Style(typeof(TextBlock)),
                    },
                    Margin = new Thickness(0, 2, 0, 2),
                };
            case UiButtonNode button:
                System.Windows.Controls.Button control = new()
                {
                    Content = button.Text,
                    Margin = new Thickness(2),
                    Padding = new Thickness(10, 4, 10, 4),
                    MinWidth = 80,
                };
                if (button.Accent && Application.Current.TryFindResource("PluginUi.AccentButton") is Style accentStyle)
                {
                    control.Style = accentStyle;
                }

                bool submit = button.SubmitForm;
                string commandId = button.CommandId;
                control.Click += (_, _) => commands.InvokeCommand(commandId, submit);
                return control;
            case UiTextNode text:
                TextBox input = new()
                {
                    Text = text.Text,
                    MinWidth = 160,
                    Tag = text.FieldId,
                    IsReadOnly = text.ReadOnly,
                };
                if (text.Multiline)
                {
                    input.AcceptsReturn = true;
                    input.TextWrapping = TextWrapping.Wrap;
                    input.MinHeight = 56;
                    input.MaxHeight = 220;
                    input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }

                if (!string.IsNullOrEmpty(text.Placeholder))
                {
                    System.Windows.Controls.ToolTipService.SetToolTip(input, text.Placeholder);
                }

                return input;

            case UiCheckBoxNode checkbox:
                CheckBox boxControl = new()
                {
                    Content = checkbox.Label,
                    IsChecked = checkbox.IsChecked,
                    Tag = checkbox.FieldId,
                    Margin = new Thickness(2),
                };
                return boxControl;
            case UiSelectNode select:
                ComboBox combo = new()
                {
                    MinWidth = 140,
                    Tag = select.FieldId,
                };
                foreach (UiSelectOption option in select.Options)
                {
                    ComboBoxItem item = new() { Content = option.Label, Tag = option.Value };
                    combo.Items.Add(item);
                    if (string.Equals(option.Value, select.Selected, StringComparison.Ordinal))
                    {
                        combo.SelectedItem = item;
                    }
                }

                return combo;
            case UiSliderNode slider:
                Slider sliderControl = new()
                {
                    Minimum = slider.Min,
                    Maximum = slider.Max,
                    SmallChange = slider.Step,
                    LargeChange = Math.Max(slider.Step, (slider.Max - slider.Min) / 10),
                    Value = Math.Clamp(slider.Value, slider.Min, slider.Max),
                    Tag = slider.FieldId,
                    IsMoveToPointEnabled = true,
                };
                if (Application.Current.TryFindResource("Style.PrecisionSlider") is Style precisionStyle)
                {
                    sliderControl.Style = precisionStyle;
                }

                return sliderControl;
            case UiListNode list:
                ListBox listBox = new()
                {
                    MaxHeight = 220,
                    MinHeight = 40,
                };
                foreach (UiListItem item in list.Items)
                {
                    TextBlock content = new()
                    {
                        Text = item.Detail is null ? item.Text : $"{item.Text} — {item.Detail}",
                        TextWrapping = TextWrapping.Wrap,
                    };
                    listBox.Items.Add(content);
                }

                return listBox;
            case UiImageNode image:
                ContentControl imageHost = new()
                {
                    MinHeight = 24,
                    MaxHeight = Math.Clamp(image.MaxHeight, 24, 1024),
                    Content = new TextBlock { Text = image.Alt ?? "[image]" },
                };
                return imageHost;
            case UiProgressNode progress:
                System.Windows.Controls.ProgressBar bar = new()
                {
                    Minimum = 0,
                    Maximum = 100,
                    Height = 18,
                };
                if (progress.Percent.HasValue)
                {
                    bar.Value = Math.Clamp(progress.Percent.Value, 0, 100);
                }

                if (!string.IsNullOrEmpty(progress.Label))
                {
                    DockPanel host = new();
                    DockPanel.SetDock(bar, Dock.Bottom);
                    host.Children.Add(new TextBlock { Text = progress.Label, Margin = new Thickness(0, 0, 0, 4) });
                    host.Children.Add(bar);
                    return host;
                }

                return bar;
            case UiDividerNode:
                return new Separator { Margin = new Thickness(0, 6, 0, 6) };
            default:
                return new TextBlock { Text = $"[unsupported node '{node.GetType().Name}']" };
        }
    }

    private static Panel MakePanel(UiDirection direction)
    {
        if (direction == UiDirection.Horizontal)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
            };
        }

        return new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
    }

    public static IReadOnlyDictionary<string, string> CollectFormValues(DependencyObject root)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        Collect(root, values);
        return values;
    }

    private static void Collect(DependencyObject current, Dictionary<string, string> values)
    {
        if (current is TextBox { Tag: string fieldId } box)
        {
            values[fieldId] = box.Text;
        }
        else if (current is CheckBox { Tag: string checkId } check)
        {
            values[checkId] = check.IsChecked == true ? "true" : "false";
        }
        else if (current is ComboBox { Tag: string comboId } combo && combo.SelectedItem is ComboBoxItem selected)
        {
            values[comboId] = selected.Tag as string ?? string.Empty;
        }
        else if (current is Slider { Tag: string sliderId } slider)
        {
            values[sliderId] = Math.Round(slider.Value, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        int children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
        for (int index = 0; index < children; index++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(current, index) is DependencyObject child)
            {
                Collect(child, values);
            }
        }
    }
}

public sealed class PluginCommandRouter
{
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<bool>> _invoker;

    internal DependencyObject? FormRoot { get; set; }

    public PluginCommandRouter(Func<string, IReadOnlyDictionary<string, string>, Task<bool>> invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invoker = invoker;
    }

    public void InvokeCommand(string commandId, bool submitForm)
    {
        IReadOnlyDictionary<string, string> values = submitForm && FormRoot is { } root
            ? PluginUiRenderer.CollectFormValues(root)
            : new Dictionary<string, string>();

        _ = _invoker(commandId, values);
    }
}
