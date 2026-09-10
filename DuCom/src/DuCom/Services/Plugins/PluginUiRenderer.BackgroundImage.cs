using System.Windows;
using System.Windows.Controls;
using DuCom.Plugin;

namespace DuCom.Services.Plugins;

public static partial class PluginUiRenderer
{
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

        if (buttons.TryGetValue("reset-defaults", out UiButtonNode? resetDefaults))
        {
            FrameworkElement resetButton = RenderNode(resetDefaults, commands);
            resetButton.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 14, 0, 0));
            resetButton.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            settings.Children.Add(resetButton);
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
            // Snap to whole percentiles like the built-in feature: the collected value, the
            // persisted 0-1 config, and the rebuilt page must round-trip exactly.
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
        };
        if (Application.Current.TryFindResource("Style.PrecisionSlider") is Style precision)
        {
            slider.Style = precision;
        }

        System.Windows.Threading.DispatcherTimer debounce = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        void UpdatePercent() => percent.Text = $"{Math.Round(slider.Value)}{node.UnitLabel ?? "%"}";
        // Only submit when the value actually moved since the last committed one: page rebuilds
        // trigger an unload-flush and must not spam duplicate apply commands.
        double committed = slider.Value;
        void Flush()
        {
            if (Math.Abs(slider.Value - committed) < 0.5) return;
            committed = slider.Value;
            commands.InvokeCommand("apply", submitForm: true);
        }

        slider.ValueChanged += (_, _) =>
        {
            UpdatePercent();
            debounce.Stop();
            debounce.Start();
        };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            Flush();
        };
        // Plugin-published state is authoritative during a rebuild. Flushing from Unloaded
        // can collect the outgoing visual tree and write stale values back over persisted data.
        slider.Unloaded += (_, _) => debounce.Stop();
        UpdatePercent();
        host.Children.Add(slider);
        percent.Margin = new Thickness(0, 4, 0, 0);
        host.Children.Add(percent);
        return host;
    }
}
