using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Services.Shortcuts;

namespace DuCom.ViewModels;

public partial class ToolCenterViewModel
{
    public ObservableCollection<ShortcutRow> FilteredShortcuts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilteredShortcuts))]
    public partial string ShortcutSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEditingShortcut { get; set; }

    [ObservableProperty]
    public partial string EditingActionName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditingGestureText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditingErrorMessage { get; set; } = string.Empty;

    partial void OnShortcutSearchTextChanged(string value) => RefreshShortcutRows();

    private void RefreshShortcutRows()
    {
        string text = ShortcutSearchText.Trim();
        IEnumerable<ShortcutDefinition> source = _shortcutManager.Definitions;
        if (!string.IsNullOrEmpty(text))
        {
            source = source.Where(definition =>
                LocalizedName(definition).Contains(text, StringComparison.OrdinalIgnoreCase) ||
                definition.GestureText.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                definition.DefaultGestureText.Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        FilteredShortcuts.Clear();
        foreach (ShortcutDefinition definition in source)
        {
            FilteredShortcuts.Add(new ShortcutRow(
                LocalizedName(definition),
                definition.GestureText,
                definition.HasConflict,
                LocalizedConflictMessage(definition),
                definition.DefaultGestureText,
                definition));
        }
    }

    private static string LocalizedName(ShortcutDefinition definition) =>
        (Application.Current.TryFindResource(definition.DisplayName) as string) ?? definition.DisplayName;

    private string LocalizedConflictMessage(ShortcutDefinition definition)
    {
        if (!definition.HasConflict || string.IsNullOrEmpty(definition.ConflictMessage))
        {
            return string.Empty;
        }

        const string prefix = "Shortcut.ConflictWith:";
        if (!definition.ConflictMessage.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (Application.Current.TryFindResource(definition.ConflictMessage) as string) ?? definition.ConflictMessage;
        }

        string[] ids = definition.ConflictMessage[prefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        string label = Application.Current.TryFindResource("Shortcut.ConflictWith") as string ?? "Conflicts with";
        string resolved = string.Join(", ", ids.Select(id =>
        {
            ShortcutDefinition? other = _shortcutManager.GetDefinition(id);
            return other is not null ? LocalizedName(other) : id;
        }));
        return $"{label}: {resolved}";
    }

    [RelayCommand]
    private void StartEditShortcut(ShortcutRow? row)
    {
        if (row is null)
        {
            return;
        }

        EditingActionName = row.ActionName;
        EditingGestureText = row.GestureText;
        EditingErrorMessage = string.Empty;
        IsEditingShortcut = true;
    }

    [RelayCommand]
    private void SaveEditedShortcut()
    {
        if (!IsEditingShortcut)
        {
            return;
        }

        ShortcutDefinition? definition = _shortcutManager.Definitions.FirstOrDefault(item =>
            LocalizedName(item) == EditingActionName);
        if (definition is null)
        {
            IsEditingShortcut = false;
            return;
        }

        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse(EditingGestureText);
        ShortcutConflictResult result = _shortcutManager.SetGesture(definition.ActionId, gesture);
        if (!result.IsValid)
        {
            EditingErrorMessage = (Application.Current.TryFindResource(result.Message) as string) ?? result.Message;
            return;
        }

        _shortcutManager.Save(ShortcutsFilePath);
        IsEditingShortcut = false;
        RefreshShortcutRows();
    }

    [RelayCommand]
    private void CancelEditShortcut() => IsEditingShortcut = false;

    [RelayCommand]
    private void ResetShortcut(ShortcutRow? row)
    {
        if (row?.Definition is null)
        {
            return;
        }

        _shortcutManager.ResetToDefault(row.Definition.ActionId);
        _shortcutManager.Save(ShortcutsFilePath);
        RefreshShortcutRows();
    }

    [RelayCommand]
    private void ResetAllShortcuts()
    {
        _shortcutManager.ResetAllToDefaults();
        _shortcutManager.Save(ShortcutsFilePath);
        RefreshShortcutRows();
    }

    private static string ShortcutsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "shortcuts.json");

    public void ApplyCapturedKeys(Key key, ModifierKeys modifiers)
    {
        if (!IsEditingShortcut)
        {
            return;
        }

        if (key == Key.Escape)
        {
            CancelEditShortcut();
            return;
        }

        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var gesture = new ShortcutKeyGesture(key.ToString(), MapModifiers(modifiers));
        if (gesture.IsModifierOnly)
        {
            return;
        }

        EditingGestureText = gesture.ToDisplayText();
    }

    private static ShortcutModifiers MapModifiers(ModifierKeys modifiers)
    {
        ShortcutModifiers result = ShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            result |= ShortcutModifiers.Ctrl;
        }

        if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
        {
            result |= ShortcutModifiers.Alt;
        }

        if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            result |= ShortcutModifiers.Shift;
        }

        if ((modifiers & ModifierKeys.Windows) == ModifierKeys.Windows)
        {
            result |= ShortcutModifiers.Win;
        }

        return result;
    }

    public sealed record ShortcutRow(
        string ActionName,
        string GestureText,
        bool HasConflict,
        string ConflictMessage,
        string DefaultGestureText,
        ShortcutDefinition Definition);
}
