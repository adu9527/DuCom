using System.Windows;
using System.Windows.Controls;
using DuCom.Plugin;

namespace DuCom.Services.Plugins;

public static partial class PluginUiRenderer
{
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
                bar.Foreground = Application.Current.TryFindResource(progress.State switch
                {
                    "running" => "Brush.Warning",
                    "succeeded" => "Brush.Success",
                    "failed" => "Brush.Danger",
                    _ => "Brush.TextMuted",
                }) as System.Windows.Media.Brush;
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
