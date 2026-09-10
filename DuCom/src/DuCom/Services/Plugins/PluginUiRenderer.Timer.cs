using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuCom.Plugin;

namespace DuCom.Services.Plugins;

public static partial class PluginUiRenderer
{
    private enum StopwatchDisplayMode
    {
        Idle,
        Running,
        Paused,
    }

    private sealed record StopwatchDisplay(StopwatchDisplayMode Mode, long AnchorUnixMs, long AccumulatedMs)
    {
        public long ElapsedMs(long nowUnixMs) => Mode switch
        {
            StopwatchDisplayMode.Running => AccumulatedMs + Math.Max(0, nowUnixMs - AnchorUnixMs),
            StopwatchDisplayMode.Paused => AccumulatedMs,
            _ => 0,
        };
    }

    private static StopwatchDisplay ParseStopwatchDisplay(string? text)
    {
        try
        {
            string[] parts = (text ?? "idle").Split(':');
            return parts[0] switch
            {
                "running" => new(StopwatchDisplayMode.Running, long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)),
                "paused" => new(StopwatchDisplayMode.Paused, 0, long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)),
                _ => new(StopwatchDisplayMode.Idle, 0, 0),
            };
        }
        catch (Exception)
        {
            return new(StopwatchDisplayMode.Idle, 0, 0);
        }
    }

    private sealed record StopwatchLapRow(int Index, long LapMs, long TotalMs, long WallClockUnixMs);

    private static List<StopwatchLapRow> ParseStopwatchLaps(UiListNode? list)
    {
        List<StopwatchLapRow> rows = [];
        if (list is null)
        {
            return rows;
        }

        foreach (UiListItem item in list.Items)
        {
            string[] parts = (item.Detail ?? string.Empty).Split('|');
            if (parts.Length != 3
                || !int.TryParse(item.Text, out int index)
                || !long.TryParse(parts[0], out long lapMs)
                || !long.TryParse(parts[1], out long totalMs)
                || !long.TryParse(parts[2], out long wallClockUnixMs))
            {
                continue;
            }

            rows.Add(new StopwatchLapRow(index, lapMs, totalMs, wallClockUnixMs));
        }

        return rows;
    }

    private static string FormatStopwatchElapsed(long milliseconds)
    {
        long clamped = Math.Max(0, milliseconds);
        long tenths = (clamped + 50) / 100;
        long tenth = tenths % 10;
        long totalSeconds = tenths / 10;
        long seconds = totalSeconds % 60;
        long minutes = (totalSeconds / 60) % 60;
        long hours = totalSeconds / 3600;
        return hours > 0
            ? $"{hours}:{minutes:D2}:{seconds:D2}.{tenth}"
            : $"{minutes:D2}:{seconds:D2}.{tenth}";
    }

    private sealed record TimerShortcut(Key Key, ModifierKeys Modifiers, string Text);

    private static TimerShortcut ReadTimerShortcut(string? value, Key fallbackKey, ModifierKeys fallbackModifiers)
    {
        if (!string.IsNullOrWhiteSpace(value) && TryParseTimerShortcut(value, out TimerShortcut shortcut))
        {
            return shortcut;
        }

        return new TimerShortcut(fallbackKey, fallbackModifiers, FormatTimerShortcut(fallbackKey, fallbackModifiers));
    }

    private static bool TryParseTimerShortcut(string value, out TimerShortcut shortcut)
    {
        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            shortcut = null!;
            return false;
        }

        ModifierKeys modifiers = ModifierKeys.None;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            switch (parts[index].ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    shortcut = null!;
                    return false;
            }
        }

        if (!Enum.TryParse(parts[^1], ignoreCase: true, out Key key) || IsModifierKey(key))
        {
            shortcut = null!;
            return false;
        }

        shortcut = new TimerShortcut(key, modifiers, FormatTimerShortcut(key, modifiers));
        return true;
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    private static string FormatTimerShortcut(Key key, ModifierKeys modifiers)
    {
        List<string> parts = [];
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key == Key.Space ? "Space" : key.ToString());
        return string.Join("+", parts);
    }

    private static FrameworkElement RenderTimer(IReadOnlyList<UiNode> nodes, PluginCommandRouter commands)
    {
        List<UiNode> flat = [];
        Flatten(nodes, flat);
        Dictionary<string, UiTextNode> fields = flat.OfType<UiTextNode>().ToDictionary(node => node.FieldId, StringComparer.Ordinal);
        Dictionary<string, UiButtonNode> buttons = flat.OfType<UiButtonNode>().ToDictionary(node => node.CommandId, StringComparer.Ordinal);
        UiListNode? lapList = flat.OfType<UiListNode>().FirstOrDefault(node => node.Id == "stopwatchLaps");
        StopwatchDisplay display = ParseStopwatchDisplay(fields.GetValueOrDefault("stopwatchState")?.Text);
        List<StopwatchLapRow> laps = ParseStopwatchLaps(lapList);
        TimerShortcutPreferences shortcutPreferences = PluginToolWindowPreferencesService.Load().TimerShortcuts ?? new();
        Dictionary<string, TimerShortcut> shortcuts = new(StringComparer.Ordinal)
        {
            ["toggle-run"] = ReadTimerShortcut(shortcutPreferences.ToggleRun, Key.Space, ModifierKeys.None),
            ["lap"] = ReadTimerShortcut(shortcutPreferences.Lap, Key.L, ModifierKeys.None),
            ["reset"] = ReadTimerShortcut(shortcutPreferences.Reset, Key.R, ModifierKeys.None),
            ["export"] = ReadTimerShortcut(shortcutPreferences.Export, Key.E, ModifierKeys.Control),
        };

        Grid root = new() { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        StackPanel top = new();
        top.Children.Add(new TextBlock
        {
            Text = Application.Current.TryFindResource("Plugins.Timer.Name") as string ?? "秒表",
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
        });

        System.Windows.Media.FontFamily mono = new("Cascadia Mono, Consolas");
        System.Windows.Threading.DispatcherTimer clockTimer = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        System.Windows.Threading.DispatcherTimer watchTimer = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };

        StackPanel clockPanel = new();
        TextBlock clockDate = new() { Style = Application.Current.TryFindResource("Style.MutedText") as Style };
        TextBlock clockTime = new() { FontFamily = mono, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
        void RefreshClock()
        {
            DateTime now = DateTime.Now;
            clockTime.Text = now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            clockDate.Text = now.ToString("yyyy-MM-dd dddd", System.Globalization.CultureInfo.CurrentCulture);
        }

        clockTimer.Tick += (_, _) => RefreshClock();
        clockPanel.Children.Add(clockDate);
        clockPanel.Children.Add(clockTime);
        top.Children.Add(Card(clockPanel, new Thickness(0, 18, 0, 0)));

        StackPanel watchPanel = new();
        TextBlock big = new()
        {
            FontFamily = mono,
            FontSize = 46,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 2),
            Text = FormatStopwatchElapsed(display.ElapsedMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
        };
        TextBlock status = new()
        {
            Text = fields.GetValueOrDefault("stopwatchStatus")?.Text,
            Style = Application.Current.TryFindResource("PluginUi.Caption") as Style,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 6, 0, 14),
        };
        Grid actions = new() { HorizontalAlignment = HorizontalAlignment.Center };
        string? editingShortcutCommand = null;
        System.Windows.Controls.Button? editingShortcutButton = null;
        foreach (string id in new[] { "toggle-run", "lap", "reset", "export" })
        {
            if (!buttons.TryGetValue(id, out UiButtonNode? button))
            {
                continue;
            }

            System.Windows.Controls.Button control = (System.Windows.Controls.Button)RenderNode(button, commands);
            control.MinWidth = 92;
            control.Padding = new Thickness(14, 6, 14, 6);
            control.Margin = new Thickness(4);
            if (id == "lap")
            {
                control.IsEnabled = display.Mode == StopwatchDisplayMode.Running;
            }
            else if (id == "reset")
            {
                control.IsEnabled = display.Mode != StopwatchDisplayMode.Idle || laps.Count > 0;
                if (string.Equals(button.Text, Application.Current.TryFindResource("Plugins.Timer.ResetConfirm") as string, StringComparison.Ordinal))
                {
                    control.SetResourceReference(Control.ForegroundProperty, "Brush.Danger");
                }
            }

            int column = actions.ColumnDefinitions.Count;
            actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel action = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2, 0, 2, 0) };
            action.Children.Add(control);
            System.Windows.Controls.Button shortcutButton = new()
            {
                Content = shortcuts[id].Text,
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(4, 2, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = Application.Current.TryFindResource("Plugins.Timer.Shortcut.EditHint") as string ?? "Click, then press a new shortcut",
            };
            shortcutButton.Click += (_, _) =>
            {
                if (editingShortcutButton is not null && editingShortcutCommand is not null)
                {
                    editingShortcutButton.Content = shortcuts[editingShortcutCommand].Text;
                }

                editingShortcutCommand = id;
                editingShortcutButton = shortcutButton;
                shortcutButton.Content = Application.Current.TryFindResource("Plugins.Timer.Shortcut.Capturing") as string ?? "Press keys...";
                shortcutButton.Focus();
            };
            action.Children.Add(shortcutButton);
            Grid.SetColumn(action, column);
            actions.Children.Add(action);
        }

        root.PreviewKeyDown += (_, eventArgs) =>
        {
            Key key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
            if (editingShortcutCommand is { } editingCommand)
            {
                eventArgs.Handled = true;
                if (key == Key.Escape)
                {
                    editingShortcutButton!.Content = shortcuts[editingCommand].Text;
                    editingShortcutCommand = null;
                    editingShortcutButton = null;
                    return;
                }

                if (IsModifierKey(key) || key == Key.Tab)
                {
                    return;
                }

                TimerShortcut updated = new(key, Keyboard.Modifiers, FormatTimerShortcut(key, Keyboard.Modifiers));
                if (shortcuts.Any(pair => pair.Key != editingCommand && pair.Value.Key == updated.Key && pair.Value.Modifiers == updated.Modifiers))
                {
                    status.Text = Application.Current.TryFindResource("Plugins.Timer.Shortcut.Conflict") as string ?? "This shortcut is already in use";
                    return;
                }

                shortcuts[editingCommand] = updated;
                PluginToolWindowPreferencesService.SaveTimerShortcut(editingCommand, updated.Text);
                editingShortcutButton!.Content = updated.Text;
                editingShortcutCommand = null;
                editingShortcutButton = null;
                return;
            }

            foreach ((string commandId, TimerShortcut shortcut) in shortcuts)
            {
                if (shortcut.Key == key && shortcut.Modifiers == Keyboard.Modifiers)
                {
                    if ((commandId == "lap" && display.Mode != StopwatchDisplayMode.Running)
                        || (commandId == "reset" && display.Mode == StopwatchDisplayMode.Idle && laps.Count == 0))
                    {
                        return;
                    }

                    eventArgs.Handled = true;
                    commands.InvokeCommand(commandId, submitForm: false);
                    return;
                }
            }
        };

        watchTimer.Tick += (_, _) => big.Text = FormatStopwatchElapsed(display.ElapsedMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        root.Loaded += (_, _) =>
        {
            RefreshClock();
            clockTimer.Start();
            watchTimer.Start();
        };
        root.Unloaded += (_, _) =>
        {
            clockTimer.Stop();
            watchTimer.Stop();
        };
        RefreshClock();
        watchPanel.Children.Add(big);
        watchPanel.Children.Add(status);
        watchPanel.Children.Add(actions);
        top.Children.Add(Card(watchPanel, new Thickness(0, 12, 0, 0)));
        root.Children.Add(top);

        Grid lapsPanel = new();
        lapsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lapsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lapsPanel.RowDefinitions.Add(new RowDefinition { Height = laps.Count == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        lapsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lapsPanel.Children.Add(Section("Plugins.Timer.Laps"));
        if (laps.Count == 0)
        {
            TextBlock empty = Muted("Plugins.Timer.LapsEmpty");
            Grid.SetRow(empty, 2);
            lapsPanel.Children.Add(empty);
        }
        else
        {
            Grid header = new Grid { Margin = new Thickness(0, 0, 6, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            string[] headerKeys = ["Plugins.Timer.Lap.Number", "Plugins.Timer.Lap.Split", "Plugins.Timer.Lap.Total", "Plugins.Timer.Lap.At"];
            for (int column = 0; column < headerKeys.Length; column++)
            {
                TextBlock headerCell = new()
                {
                    FontFamily = mono,
                    FontSize = 11,
                    Text = Application.Current.TryFindResource(headerKeys[column]) as string ?? headerKeys[column],
                    TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right,
                };
                headerCell.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                Grid.SetColumn(headerCell, column);
                header.Children.Add(headerCell);
            }
            Grid.SetRow(header, 1);
            lapsPanel.Children.Add(header);
            StackPanel rows = new();
            for (int index = 0; index < laps.Count; index++)
            {
                StopwatchLapRow lap = laps[index];
                Grid row = new() { Margin = new Thickness(0, 3, 6, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
                DateTime wallClock = DateTimeOffset.FromUnixTimeMilliseconds(lap.WallClockUnixMs).LocalDateTime;
                string[] values =
                [
                    $"#{lap.Index}",
                    FormatStopwatchElapsed(lap.LapMs),
                    FormatStopwatchElapsed(lap.TotalMs),
                    wallClock.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                ];
                for (int column = 0; column < values.Length; column++)
                {
                    TextBlock cell = new()
                    {
                        FontFamily = mono,
                        FontSize = 13,
                        Text = values[column],
                        TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right,
                    };
                    cell.SetResourceReference(TextBlock.ForegroundProperty, column is 0 or 3 ? "Brush.TextSecondary" : "Brush.TextPrimary");
                    Grid.SetColumn(cell, column);
                    row.Children.Add(cell);
                }
                rows.Children.Add(row);
            }

            ScrollViewer lapScroller = new()
            {
                Content = rows,
                Margin = new Thickness(0, 2, 6, 0),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            Grid.SetRow(lapScroller, 2);
            lapsPanel.Children.Add(lapScroller);
            if (laps.Count >= 2)
            {
                StopwatchLapRow fastest = laps[0];
                StopwatchLapRow slowest = laps[0];
                long sum = 0;
                foreach (StopwatchLapRow lap in laps)
                {
                    if (lap.LapMs < fastest.LapMs) fastest = lap;
                    if (lap.LapMs > slowest.LapMs) slowest = lap;
                    sum += lap.LapMs;
                }

                Grid summary = new() { Margin = new Thickness(0, 10, 6, 0) };
                for (int column = 0; column < 3; column++)
                {
                    summary.ColumnDefinitions.Add(new ColumnDefinition());
                }

                string[] statisticLabels =
                [
                    Application.Current.TryFindResource("Plugins.Timer.Statistic.Fastest") as string ?? "Fastest",
                    Application.Current.TryFindResource("Plugins.Timer.Statistic.Slowest") as string ?? "Slowest",
                    Application.Current.TryFindResource("Plugins.Timer.Statistic.Average") as string ?? "Average",
                ];
                string[] statisticValues =
                [
                    $"#{fastest.Index}  {FormatStopwatchElapsed(fastest.LapMs)}",
                    $"#{slowest.Index}  {FormatStopwatchElapsed(slowest.LapMs)}",
                    FormatStopwatchElapsed((long)Math.Round(sum / (double)laps.Count, MidpointRounding.AwayFromZero)),
                ];
                for (int column = 0; column < statisticLabels.Length; column++)
                {
                    StackPanel statistic = new();
                    TextBlock label = new()
                    {
                        Text = statisticLabels[column],
                        Style = Application.Current.TryFindResource("Style.MutedText") as Style,
                        FontSize = 11,
                    };
                    TextBlock value = new()
                    {
                        Text = statisticValues[column],
                        FontFamily = mono,
                        FontSize = 12,
                        Margin = new Thickness(0, 2, 0, 0),
                    };
                    statistic.Children.Add(label);
                    statistic.Children.Add(value);
                    Grid.SetColumn(statistic, column);
                    summary.Children.Add(statistic);
                }
                Grid.SetRow(summary, 3);
                lapsPanel.Children.Add(summary);
            }
        }

        Border lapsCard = Card(lapsPanel, new Thickness(0, 12, 0, 0));
        Grid.SetRow(lapsCard, 1);
        root.Children.Add(lapsCard);

        commands.FormRoot = root;
        ApplyTheme(root);
        big.SetResourceReference(TextBlock.ForegroundProperty, display.Mode switch
        {
            StopwatchDisplayMode.Running => "Brush.Accent",
            StopwatchDisplayMode.Paused => "Brush.Caution",
            _ => "Brush.TextPrimary",
        });
        return root;
    }
}
