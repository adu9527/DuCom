using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        if (string.Equals(pluginId, "com.ducom.timer", StringComparison.Ordinal))
        {
            return RenderStopwatch(nodes, commands);
        }

        if (string.Equals(pluginId, "com.ducom.timer", StringComparison.Ordinal))
        {
            return RenderTimer(nodes, commands);
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
