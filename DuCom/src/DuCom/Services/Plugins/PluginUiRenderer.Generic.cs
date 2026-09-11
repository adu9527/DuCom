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
            if (text.Tag is UiTextStyle.Success) brush = "Brush.Success";
            else if (text.Style is { } style)
            {
                if (style == Application.Current.TryFindResource("Style.MutedText") || style == Application.Current.TryFindResource("PluginUi.Caption")) brush = "Brush.TextSecondary";
                else if (style == Application.Current.TryFindResource("PluginUi.Accent")) brush = "Brush.Accent";
                else if (style == Application.Current.TryFindResource("PluginUi.Success")) brush = "Brush.Success";
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
            else if (node is UiExpanderNode expander) Flatten(expander.Children, result);
            else result.Add(node);
        }
    }

    private static Border Card(UIElement child, Thickness? margin = null, bool compact = false)
    {
        Border card = new()
        {
            Child = child,
            Margin = margin ?? new Thickness(0),
            Style = Application.Current.TryFindResource("Style.SettingsCard") as Style,
        };
        if (compact) card.Padding = new Thickness(10, 7, 10, 7);
        return card;
    }

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
                Panel container = MakePanel(panel.Direction, panel.ItemWidth, panel.Wrap, panel.Compact, panel.VerticalCenter);
                if (panel.Width is { } panelWidth) container.Width = panelWidth;
                foreach (UiNode child in panel.Children)
                {
                    container.Children.Add(RenderNode(child, commands));
                }

                if (panel.Presentation != UiPanelPresentation.Card)
                {
                    return container;
                }

                StackPanel cardContent = new();
                if (!string.IsNullOrWhiteSpace(panel.Title))
                {
                    cardContent.Children.Add(new TextBlock
                    {
                        Text = panel.Title,
                        Style = Application.Current.TryFindResource("Style.SectionTitle") as Style,
                        Margin = new Thickness(0, 0, 0, 10),
                    });
                }
                cardContent.Children.Add(container);
                return Card(cardContent, new Thickness(0, 0, 0, panel.Compact ? 6 : 12), panel.Compact);
            case UiExpanderNode expander:
                StackPanel expanderContent = new();
                foreach (UiNode child in expander.Children)
                {
                    expanderContent.Children.Add(RenderNode(child, commands));
                }
                return new Expander
                {
                    Header = expander.Title,
                    IsExpanded = expander.IsExpanded,
                    Content = expanderContent,
                    Margin = new Thickness(0, 3, 0, 8),
                };
            case UiLabelNode label:
                TextBlock labelControl = new()
                {
                    Text = label.Text,
                    Tag = label.Style,
                    TextWrapping = label.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    Style = label.Style switch
                    {
                        UiTextStyle.Heading => (Style)Application.Current.TryFindResource("PluginUi.Heading") ?? new Style(typeof(TextBlock)),
                        UiTextStyle.Caption => (Style)Application.Current.TryFindResource("PluginUi.Caption") ?? new Style(typeof(TextBlock)),
                        UiTextStyle.Accent => (Style)Application.Current.TryFindResource("PluginUi.Accent") ?? new Style(typeof(TextBlock)),
                        UiTextStyle.Success => SuccessTextStyle(),
                        UiTextStyle.Warning => (Style)Application.Current.TryFindResource("PluginUi.Warning") ?? new Style(typeof(TextBlock)),
                        _ => (Style)Application.Current.TryFindResource("PluginUi.Normal") ?? new Style(typeof(TextBlock)),
                    },
                    Margin = new Thickness(0, 2, 0, 2),
                    VerticalAlignment = label.VerticalCenter ? VerticalAlignment.Center : VerticalAlignment.Top,
                };
                if (label.FontSizeDelta is { } fontSizeDelta)
                    labelControl.FontSize = Math.Max(1, labelControl.FontSize + fontSizeDelta);
                if (label.MaxWidth is { } labelMaxWidth)
                {
                    labelControl.MaxWidth = labelMaxWidth;
                    labelControl.TextTrimming = TextTrimming.CharacterEllipsis;
                    ToolTipService.SetToolTip(labelControl, label.Text);
                }
                return labelControl;
            case UiButtonNode button:
                System.Windows.Controls.Button control = new()
                {
                    Content = button.Text,
                    Margin = new Thickness(2),
                    Padding = new Thickness(10, 4, 10, 4),
                    MinWidth = 80,
                    IsEnabled = button.IsEnabled,
                };
                if (button.Accent && Application.Current.TryFindResource("PluginUi.AccentButton") is Style accentStyle)
                {
                    control.Style = accentStyle;
                }

                bool submit = button.SubmitForm;
                string commandId = button.CommandId;
                void Invoke()
                {
                    if (button.DisableOnClick) control.IsEnabled = false;
                    commands.InvokeCommand(commandId, submit);
                }
                if (button.InvokeOnPress)
                {
                    control.PreviewMouseLeftButtonDown += (_, args) =>
                    {
                        args.Handled = true;
                        Invoke();
                    };
                    control.Click += (_, _) =>
                    {
                        if (System.Windows.Input.Mouse.LeftButton != System.Windows.Input.MouseButtonState.Pressed) Invoke();
                    };
                }
                else control.Click += (_, _) => Invoke();
                return control;
            case UiTextNode text:
                TextBox input = new()
                {
                    Text = text.Text,
                    MinWidth = 160,
                    Tag = text.FieldId,
                    IsReadOnly = text.ReadOnly,
                    IsEnabled = text.IsEnabled,
                };
                if (text.Width is { } textWidth)
                {
                    input.Width = textWidth;
                    input.MinWidth = 0;
                }
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
                    TextBlock placeholder = new()
                    {
                        Text = text.Placeholder,
                        IsHitTestVisible = false,
                        Margin = new Thickness(5, 2, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed,
                    };
                    placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                    input.TextChanged += (_, _) => placeholder.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed;
                    Grid inputHost = new();
                    inputHost.Children.Add(input);
                    inputHost.Children.Add(placeholder);
                    return inputHost;
                }

                return input;

            case UiCheckBoxNode checkbox:
                CheckBox boxControl = new()
                {
                    Content = checkbox.Label,
                    IsChecked = checkbox.IsChecked,
                    Tag = checkbox.FieldId,
                    Margin = new Thickness(2),
                    IsEnabled = checkbox.IsEnabled,
                };
                if (!string.IsNullOrWhiteSpace(checkbox.CommandId))
                {
                    string changedCommandId = checkbox.CommandId;
                    bool submitOnChange = checkbox.SubmitOnChange;
                    RoutedEventHandler changed = (_, _) => commands.InvokeCommand(changedCommandId, submitOnChange);
                    boxControl.Checked += changed;
                    boxControl.Unchecked += changed;
                }
                return boxControl;
            case UiSelectNode select:
                ComboBox combo = new()
                {
                    MinWidth = 140,
                    Tag = select.FieldId,
                    IsEnabled = select.IsEnabled,
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
                    Height = progress.Height ?? 18,
                    VerticalAlignment = VerticalAlignment.Center,
                    BorderThickness = new Thickness(1),
                };
                bar.SetResourceReference(Control.BackgroundProperty, "Brush.PanelRaised");
                bar.SetResourceReference(Control.BorderBrushProperty, "Brush.PanelBorder");
                if (progress.Width is { } progressWidth) bar.Width = progressWidth;
                if (!string.IsNullOrWhiteSpace(progress.Tooltip))
                    ToolTipService.SetToolTip(bar, progress.Tooltip);
                bar.SetResourceReference(Control.ForegroundProperty, progress.State switch
                {
                    "running" => "Brush.Warning",
                    "calibrating" or "calibrated" => "Brush.Accent",
                    "succeeded" => "Brush.Success",
                    "failed" or "cancelled" => "Brush.Danger",
                    _ => "Brush.TextMuted",
                });
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

    private static Panel MakePanel(UiDirection direction, double? itemWidth = null, bool wrap = true, bool compact = false, bool verticalCenter = false)
    {
        if (direction == UiDirection.Horizontal)
        {
            if (!wrap)
            {
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = compact ? new Thickness(0) : new Thickness(0, 3, 0, 3),
                    VerticalAlignment = verticalCenter ? VerticalAlignment.Center : VerticalAlignment.Top,
                };
            }

            WrapPanel panel = new()
            {
                Orientation = Orientation.Horizontal,
                Margin = compact ? new Thickness(0) : new Thickness(0, 3, 0, 3),
                VerticalAlignment = verticalCenter ? VerticalAlignment.Center : VerticalAlignment.Top,
            };
            if (itemWidth is { } width) panel.ItemWidth = width;
            return panel;
        }

        return new StackPanel { Margin = compact ? new Thickness(0) : new Thickness(0, 3, 0, 3) };
    }

    private static Style SuccessTextStyle()
    {
        if (Application.Current.TryFindResource("PluginUi.Success") is Style style) return style;
        Style fallback = new(typeof(TextBlock));
        fallback.Setters.Add(new Setter(TextBlock.ForegroundProperty, Application.Current.TryFindResource("Brush.Success")));
        return fallback;
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
