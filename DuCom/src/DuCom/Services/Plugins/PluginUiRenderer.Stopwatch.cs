using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuCom.Plugin;

namespace DuCom.Services.Plugins;

public static partial class PluginUiRenderer
{
    /// <summary>
    /// Stopwatch page: a top bar with the always-on-top toggle and shortcut hints, a live
    /// ticking display driven by the plugin's state string, formatted lap rows, and the
    /// plugin's action buttons. Space toggles run, L records a lap, R resets (double-press).
    /// </summary>
    private static FrameworkElement RenderStopwatch(IReadOnlyList<UiNode> nodes, PluginCommandRouter commands)
    {
        List<UiNode> flat = [];
        Flatten(nodes, flat);
        Dictionary<string, UiButtonNode> buttons = flat.OfType<UiButtonNode>().ToDictionary(node => node.CommandId, StringComparer.Ordinal);
        UiTextNode? stateField = flat.OfType<UiTextNode>().FirstOrDefault(node => node.FieldId == "stopwatchState");
        UiTextNode? statusField = flat.OfType<UiTextNode>().FirstOrDefault(node => node.FieldId == "stopwatchStatus");
        UiListNode? lapsField = flat.OfType<UiListNode>().FirstOrDefault(node => node.Id == "stopwatchLaps");
        string stateText = stateField?.Text ?? "idle";

        StackPanel root = new() { MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Stretch };

        Border clockCard = new()
        {
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(18, 12, 18, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
        };
        clockCard.SetResourceReference(Border.BackgroundProperty, "Brush.PanelRaised");
        clockCard.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
        TextBlock clock = new()
        {
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 44,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        TextBlock clockCaption = new()
        {
            Style = Application.Current.TryFindResource("Style.MutedText") as Style,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        StackPanel clockStack = new();
        clockStack.Children.Add(clock);
        clockStack.Children.Add(clockCaption);
        clockCard.Child = clockStack;
        root.Children.Add(clockCard);

        System.Windows.Threading.DispatcherTimer ticker = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(31) };
        void RenderState()
        {
            string current = stateField?.Text ?? "idle";
            string[] parts = current.Split(':');
            switch (parts)
            {
                case { Length: 3 } when parts[0] == "running" && long.TryParse(parts[1], out long anchor) && long.TryParse(parts[2], out long accumulated):
                    clock.Text = FormatStopwatchSpan(accumulated + Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - anchor));
                    clockCaption.Text = Application.Current.TryFindResource("Plugins.Stopwatch.Running") as string ?? "运行中";
                    break;
                case { Length: 2 } when parts[0] == "paused" && long.TryParse(parts[1], out long paused):
                    clock.Text = FormatStopwatchSpan(paused);
                    clockCaption.Text = Application.Current.TryFindResource("Plugins.Stopwatch.Paused") as string ?? "已暂停";
                    break;
                default:
                    clock.Text = "00:00:00.000";
                    clockCaption.Text = Application.Current.TryFindResource("Plugins.Stopwatch.Idle") as string ?? "待机";
                    break;
            }
        }

        ticker.Tick += (_, _) => RenderState();
        root.Loaded += (_, _) => { RenderState(); ticker.Start(); };
        root.Unloaded += (_, _) => ticker.Stop();

        Grid actions = new() { Margin = new Thickness(0, 16, 0, 0) };
        string[] actionIds = ["toggle-run", "lap", "reset", "export-csv", "export-xlsx"];
        foreach (string _ in actionIds)
        {
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int index = 0; index < actionIds.Length; index++)
        {
            if (!buttons.TryGetValue(actionIds[index], out UiButtonNode? button)) continue;
            System.Windows.Controls.Button control = (System.Windows.Controls.Button)RenderNode(button, commands);
            control.MinHeight = 42;
            control.FontSize = 15;
            control.Padding = new Thickness(8, 6, 8, 6);
            control.Margin = new Thickness(5, 0, 5, 0);
            control.HorizontalContentAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(control, index);
            actions.Children.Add(control);
        }

        root.Children.Add(actions);

        // Editable shortcut keys: same five-column grid as the button row, each key button sits
        // directly below its action button (no text labels; the tooltip names the action).
        StopwatchShortcuts shortcuts = StopwatchShortcuts.Load();
        string? capturing = null;
        System.Windows.Controls.Button toggleKeyButton = new();
        System.Windows.Controls.Button lapKeyButton = new();
        System.Windows.Controls.Button resetKeyButton = new();
        void RefreshShortcutLabels()
        {
            toggleKeyButton.Content = StopwatchShortcuts.Display(shortcuts.ToggleRun);
            lapKeyButton.Content = StopwatchShortcuts.Display(shortcuts.Lap);
            resetKeyButton.Content = StopwatchShortcuts.Display(shortcuts.Reset);
        }

        Grid shortcutBar = new() { Margin = new Thickness(5, 10, 5, 16) };
        for (int index = 0; index < actionIds.Length; index++)
        {
            shortcutBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        void AddShortcutKey(int column, string tooltipResource, string tooltipFallback, System.Windows.Controls.Button keyButton)
        {
            keyButton.MinWidth = 64;
            keyButton.Padding = new Thickness(10, 3, 10, 3);
            // Sit under the action button's leading edge rather than dead center: reads as a
            // caption of the button above and nudges the key chips visually to the left.
            keyButton.HorizontalAlignment = HorizontalAlignment.Left;
            keyButton.Margin = new Thickness(14, 0, 0, 0);
            keyButton.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            System.Windows.Controls.ToolTipService.SetToolTip(keyButton, Application.Current.TryFindResource(tooltipResource) as string ?? tooltipFallback);
            keyButton.Click += (_, _) =>
            {
                capturing = ReferenceEquals(keyButton, toggleKeyButton) ? "toggle-run"
                    : ReferenceEquals(keyButton, lapKeyButton) ? "lap"
                    : "reset";
                keyButton.Content = Application.Current.TryFindResource("Plugins.Stopwatch.PressKeys") as string ?? "…";
            };
            Grid.SetColumn(keyButton, column);
            shortcutBar.Children.Add(keyButton);
        }

        AddShortcutKey(0, "Plugins.Stopwatch.ActionToggle", "开始/暂停", toggleKeyButton);
        AddShortcutKey(1, "Plugins.Stopwatch.ActionLap", "计次", lapKeyButton);
        AddShortcutKey(2, "Plugins.Stopwatch.ActionReset", "重置", resetKeyButton);
        RefreshShortcutLabels();
        root.Children.Add(shortcutBar);

        if (lapsField is not null)
        {
            Grid headerRow = NewLapGrid(isHeader: true);
            headerRow.Children.Add(NewLapCell(Application.Current.TryFindResource("Plugins.Stopwatch.Laps.Index") as string ?? "#", 0, isHeader: true));
            headerRow.Children.Add(NewLapCell(Application.Current.TryFindResource("Plugins.Stopwatch.Laps.Lap") as string ?? "单次", 1, isHeader: true));
            headerRow.Children.Add(NewLapCell(Application.Current.TryFindResource("Plugins.Stopwatch.Laps.Total") as string ?? "总计", 2, isHeader: true));

            StackPanel rows = new();
            bool first = true;
            foreach (UiListItem item in lapsField.Items)
            {
                string detail = item.Detail ?? string.Empty;
                string[] segments = detail.Split('|');
                string lapText = segments.Length > 0 && long.TryParse(segments[0], out long lapMs) ? FormatStopwatchSpan(lapMs) : detail;
                string totalText = segments.Length > 1 && long.TryParse(segments[1], out long totalMs) ? FormatStopwatchSpan(totalMs) : string.Empty;
                Grid row = NewLapGrid(isHeader: false);
                if (first)
                {
                    row.SetResourceReference(Panel.BackgroundProperty, "Brush.AccentSoft");
                    first = false;
                }

                row.Children.Add(NewLapCell($"#{item.Text}", 0, isHeader: false));
                row.Children.Add(NewLapCell(lapText, 1, isHeader: false));
                row.Children.Add(NewLapCell(totalText, 2, isHeader: false));
                rows.Children.Add(row);
            }

            // Header and rows share one container width and identical column definitions, so the
            // columns line up; only the row area scrolls.
            ScrollViewer lapsScroll = new()
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = rows,
            };
            StackPanel cardStack = new();
            cardStack.Children.Add(headerRow);
            cardStack.Children.Add(lapsScroll);

            Border lapsCard = new()
            {
                Child = cardStack,
                Padding = new Thickness(12, 6, 12, 6),
            };
            lapsCard.SetResourceReference(Border.BackgroundProperty, "Brush.PanelRaised");
            lapsCard.SetResourceReference(Border.BorderBrushProperty, "Brush.PanelBorder");
            root.Children.Add(lapsCard);
        }

        if (statusField is not null)
        {
            root.Children.Add(new TextBlock
            {
                Text = statusField.Text,
                Style = Application.Current.TryFindResource("Style.MutedText") as Style,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8),
            });
        }

        static Key RealKey(System.Windows.Input.KeyEventArgs e) => e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        bool MatchesGesture(string gesture, System.Windows.Input.KeyEventArgs e)
        {
            if (!StopwatchShortcuts.TryParse(gesture, out Key key, out System.Windows.Input.ModifierKeys modifiers))
            {
                return false;
            }

            if (RealKey(e) != key)
            {
                return false;
            }

            const System.Windows.Input.ModifierKeys Relevant = System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt | System.Windows.Input.ModifierKeys.Shift;
            return (System.Windows.Input.Keyboard.Modifiers & Relevant) == modifiers;
        }

        // Window-level shortcuts; PreviewKeyDown runs before focused buttons handle Space.
        void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (capturing is not null)
            {
                e.Handled = true;
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    capturing = null;
                    RefreshShortcutLabels();
                    return;
                }

                Key pressed = RealKey(e);
                if (pressed is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
                    or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
                    or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
                    or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
                {
                    return; // wait for the actual key; modifiers combine with it.
                }

                string gesture = StopwatchShortcuts.FromEventArgs(System.Windows.Input.Keyboard.Modifiers, pressed);
                shortcuts = capturing switch
                {
                    "toggle-run" => shortcuts with { ToggleRun = gesture },
                    "lap" => shortcuts with { Lap = gesture },
                    _ => shortcuts with { Reset = gesture },
                };
                StopwatchShortcuts.Save(shortcuts);
                capturing = null;
                RefreshShortcutLabels();
                return;
            }

            if (System.Windows.Input.Keyboard.FocusedElement is TextBox)
            {
                return;
            }

            if (MatchesGesture(shortcuts.ToggleRun, e))
            {
                e.Handled = true;
                if (buttons.ContainsKey("toggle-run")) commands.InvokeCommand("toggle-run", submitForm: false);
            }
            else if (MatchesGesture(shortcuts.Lap, e))
            {
                e.Handled = true;
                if (buttons.ContainsKey("lap")) commands.InvokeCommand("lap", submitForm: false);
            }
            else if (MatchesGesture(shortcuts.Reset, e))
            {
                e.Handled = true;
                if (buttons.ContainsKey("reset")) commands.InvokeCommand("reset", submitForm: false);
            }
        }

        root.Loaded += (_, _) =>
        {
            if (Window.GetWindow(root) is { } window) window.PreviewKeyDown += OnPreviewKeyDown;
        };
        root.Unloaded += (_, _) =>
        {
            if (Window.GetWindow(root) is { } window) window.PreviewKeyDown -= OnPreviewKeyDown;
        };

        commands.FormRoot = root;
        ApplyTheme(root);
        return root;
    }

    private static string FormatStopwatchSpan(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A three-column row (index / lap / total) styled like online stopwatches.</summary>
    private static Grid NewLapGrid(bool isHeader)
    {
        Grid grid = new()
        {
            Margin = new Thickness(0, isHeader ? 2 : 1, 0, isHeader ? 4 : 1),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private static TextBlock NewLapCell(string text, int column, bool isHeader)
    {
        TextBlock cell = new()
        {
            Text = text,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = isHeader ? 12.5 : 14,
            Foreground = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(6, isHeader ? 2 : 5, 6, isHeader ? 2 : 5),
            VerticalAlignment = VerticalAlignment.Center,
            // Right-align the numeric columns so header and data columns read as one table.
            TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right,
        };
        cell.SetResourceReference(TextBlock.ForegroundProperty, isHeader ? "Brush.TextSecondary" : "Brush.TextPrimary");
        if (isHeader)
        {
            cell.FontWeight = FontWeights.SemiBold;
        }

        Grid.SetColumn(cell, column);
        return cell;
    }

}
