using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Services.Shortcuts;

namespace DuCom.ViewModels;

/// <summary>
/// ViewModel for the shortcuts page inside the settings window. It wraps the shared
/// <see cref="ShortcutManager"/> instance owned by <see cref="MainViewModel"/>, so edits
/// here take effect immediately in the main window and persist to the same shortcuts file.
/// </summary>
public partial class ShortcutsSettingsViewModel : ObservableObject
{
    private readonly ShortcutManager _shortcutManager;
    private ShortcutDefinition? _editingDefinition;

    public ShortcutsSettingsViewModel(ShortcutManager shortcutManager)
    {
        _shortcutManager = shortcutManager ?? throw new ArgumentNullException(nameof(shortcutManager));
        RefreshShortcutRows();
    }

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
                definition.IsEnabled,
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
    private void ToggleShortcutEnabled(ShortcutRow? row)
    {
        if (row?.Definition is null)
        {
            return;
        }

        bool previous = row.Definition.IsEnabled;
        _shortcutManager.SetEnabled(row.Definition.ActionId, !previous);
        if (!TrySave(() => _shortcutManager.SetEnabled(row.Definition.ActionId, previous)))
        {
            return;
        }

        RefreshShortcutRows();
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
        _editingDefinition = row.Definition;
        IsEditingShortcut = true;
    }

    [RelayCommand]
    private void SaveEditedShortcut()
    {
        if (!IsEditingShortcut)
        {
            return;
        }

        ShortcutDefinition? definition = _editingDefinition;
        if (definition is null)
        {
            EditingErrorMessage = Resource("Shortcut.ActionNotFound");
            return;
        }

        ShortcutKeyGesture? gesture = ShortcutKeyGesture.Parse(EditingGestureText);
        ShortcutKeyGesture? previous = definition.Gesture;
        ShortcutConflictResult result = _shortcutManager.SetGesture(definition.ActionId, gesture);
        if (!result.IsValid)
        {
            EditingErrorMessage = (Application.Current.TryFindResource(result.Message) as string) ?? result.Message;
            return;
        }

        if (!TrySave(() => _shortcutManager.SetGesture(definition.ActionId, previous)))
        {
            return;
        }

        _editingDefinition = null;
        IsEditingShortcut = false;
        RefreshShortcutRows();
    }

    [RelayCommand]
    private void CancelEditShortcut()
    {
        _editingDefinition = null;
        EditingErrorMessage = string.Empty;
        IsEditingShortcut = false;
    }

    [RelayCommand]
    private void ClearEditedShortcut()
    {
        EditingGestureText = string.Empty;
        EditingErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void ResetShortcut(ShortcutRow? row)
    {
        if (row?.Definition is null)
        {
            return;
        }

        ShortcutKeyGesture? previous = row.Definition.Gesture;
        _shortcutManager.ResetToDefault(row.Definition.ActionId);
        if (!TrySave(() => _shortcutManager.SetGesture(row.Definition.ActionId, previous)))
        {
            return;
        }

        RefreshShortcutRows();
    }

    [RelayCommand]
    private void ResetAllShortcuts()
    {
        Dictionary<string, ShortcutKeyGesture?> previous = _shortcutManager.Definitions
            .ToDictionary(item => item.ActionId, item => item.Gesture, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> previousEnabled = _shortcutManager.Definitions
            .ToDictionary(item => item.ActionId, item => item.IsEnabled, StringComparer.OrdinalIgnoreCase);
        _shortcutManager.ResetAllToDefaults();
        if (!TrySave(() =>
            {
                foreach ((string actionId, ShortcutKeyGesture? gesture) in previous)
                {
                    _shortcutManager.SetGesture(actionId, gesture);
                    _shortcutManager.SetEnabled(actionId, previousEnabled[actionId]);
                }
            }))
        {
            return;
        }

        RefreshShortcutRows();
    }

    public void CancelPendingEdit() => CancelEditShortcut();

    private bool TrySave(Action rollback)
    {
        try
        {
            _shortcutManager.Save(ShortcutsFilePath);
            EditingErrorMessage = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            rollback();
            EditingErrorMessage = Resource("Shortcut.SaveFailed").Replace("{0}", exception.Message, StringComparison.Ordinal);
            Program.DiagnosticLog?.Warning($"Failed to save shortcuts. {exception.Message}");
            RefreshShortcutRows();
            return false;
        }
    }

    private static string Resource(string key) =>
        (Application.Current.TryFindResource(key) as string) ?? key;

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
        bool IsEnabled,
        bool HasConflict,
        string ConflictMessage,
        string DefaultGestureText,
        ShortcutDefinition Definition);
}
