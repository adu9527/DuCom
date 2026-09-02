using System.IO;
using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Services.Shortcuts;

public sealed class ShortcutManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
    private readonly List<ShortcutAction> _actions = [];
    private readonly List<ShortcutDefinition> _definitions = [];
    private readonly Dictionary<string, ShortcutDefinition> _definitionsById = [];

    public IReadOnlyList<ShortcutDefinition> Definitions => _definitions;

    public void RegisterDefaultActions()
    {
        Register(new("OpenCloseSelectedPort", "Shortcut.OpenCloseSelectedPort", "Ctrl+Enter", "Shortcut.Category.Session"));
        Register(new("RefreshPorts", "Shortcut.RefreshPorts", "F5", "Shortcut.Category.General"));
        Register(new("ClearDisplay", "Shortcut.ClearDisplay", "Ctrl+L", "Shortcut.Category.Display"));
        Register(new("SaveVisibleLog", "Shortcut.SaveVisibleLog", "Ctrl+S", "Shortcut.Category.File"));
        Register(new("ToggleFollowEnd", "Shortcut.ToggleFollowEnd", "Ctrl+P", "Shortcut.Category.Display"));
        Register(new("ToggleSidebar", "Shortcut.ToggleSidebar", "Ctrl+B", "Shortcut.Category.View"));
        Register(new("OpenSearch", "Shortcut.OpenSearch", "Ctrl+F", "Shortcut.Category.Display"));
        Register(new("OpenTools", "Shortcut.OpenTools", "Ctrl+T", "Shortcut.Category.Tools"));
        Register(new("MaximizeRestore", "Shortcut.MaximizeRestore", "F11", "Shortcut.Category.Window"));
        Register(new("FocusSendEditor", "Shortcut.FocusSendEditor", "Ctrl+D", "Shortcut.Category.Send"));
        Register(new("CloseRightPane", "Shortcut.CloseRightPane", "Ctrl+Shift+W", "Shortcut.Category.Window"));
        Register(new("CloseSelectedSession", "Shortcut.CloseSelectedSession", "Ctrl+W", "Shortcut.Category.Session"));
        Register(new("ToggleHexDisplay", "Shortcut.ToggleHexDisplay", "Alt+E", "Shortcut.Category.Display"));
        Register(new("ToggleTimestamp", "Shortcut.ToggleTimestamp", "Alt+D", "Shortcut.Category.Display"));
        Register(new("ToggleSendMode", "Shortcut.ToggleSendMode", "Ctrl+Shift+M", "Shortcut.Category.Send"));
        Register(new("FormatJson", "Shortcut.FormatJson", string.Empty, "Shortcut.Category.Edit"));
        Register(new("JoinLines", "Shortcut.JoinLines", string.Empty, "Shortcut.Category.Edit"));
    }

    public void Register(ShortcutAction action)
    {
        if (_actions.Any(existing => string.Equals(existing.ActionId, action.ActionId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Duplicate shortcut action registration: {action.ActionId}");
        }

        _actions.Add(action);
        var definition = new ShortcutDefinition(action, action.DisplayNameKey);
        _definitions.Add(definition);
        _definitionsById[action.ActionId] = definition;
        definition.ResetToDefault();
    }

    public bool TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            ResetAllToDefaults();
            return false;
        }

        try
        {
            string json = File.ReadAllText(path);
            ShortcutConfiguration? configuration = JsonSerializer.Deserialize<ShortcutConfiguration>(json, JsonOptions);
            if (configuration is null || configuration.Version < 1)
            {
                ResetAllToDefaults();
                return false;
            }

            ApplyConfiguration(configuration);
            RebuildConflicts();
            return true;
        }
        catch (JsonException)
        {
            ResetAllToDefaults();
            return false;
        }
    }

    public void Save(string path)
    {
        ShortcutConfiguration configuration = CaptureConfiguration();
        AtomicFileStore.CommitAll([
            new AtomicFileWrite(path, AtomicFileStore.EncodeUtf8(JsonSerializer.Serialize(configuration, JsonOptions))),
        ]);
    }

    public ShortcutConflictResult SetGesture(string actionId, ShortcutKeyGesture? gesture)
    {
        if (!_definitionsById.TryGetValue(actionId, out ShortcutDefinition? definition))
        {
            return ShortcutConflictResult.Invalid("Shortcut.ActionNotFound");
        }

        if (gesture is null || gesture.IsEmpty)
        {
            definition.Gesture = null;
            RebuildConflicts();
            return ShortcutConflictResult.Valid();
        }

        if (gesture.IsModifierOnly)
        {
            return ShortcutConflictResult.Invalid("Shortcut.ModifierOnlyNotAllowed");
        }

        List<string> conflicts = _definitions
            .Where(other =>
                !string.Equals(other.ActionId, actionId, StringComparison.OrdinalIgnoreCase) &&
                definition.IsEnabled &&
                other.IsEnabled &&
                other.Gesture is not null &&
                other.Gesture.Matches(gesture))
            .Select(other => other.ActionId)
            .ToList();

        if (conflicts.Count > 0)
        {
            return ShortcutConflictResult.Invalid("Shortcut.ConflictDetected", conflicts);
        }

        definition.Gesture = gesture;
        RebuildConflicts();
        return ShortcutConflictResult.Valid();
    }

    public void SetEnabled(string actionId, bool isEnabled)
    {
        if (_definitionsById.TryGetValue(actionId, out ShortcutDefinition? definition))
        {
            definition.IsEnabled = isEnabled;
            RebuildConflicts();
        }
    }

    public void ResetToDefault(string actionId)
    {
        if (_definitionsById.TryGetValue(actionId, out ShortcutDefinition? definition))
        {
            definition.ResetToDefault();
            RebuildConflicts();
        }
    }

    public void ResetAllToDefaults()
    {
        foreach (ShortcutDefinition definition in _definitions)
        {
            definition.ResetToDefault();
        }

        RebuildConflicts();
    }

    public string? FindActionId(ShortcutKeyGesture gesture)
    {
        return _definitions
            .FirstOrDefault(definition =>
                definition.IsEnabled &&
                definition.Gesture is not null &&
                definition.Gesture.Matches(gesture) &&
                !definition.HasConflict)
            ?.ActionId;
    }

    public ShortcutDefinition? GetDefinition(string actionId) =>
        _definitionsById.TryGetValue(actionId, out ShortcutDefinition? definition) ? definition : null;

    private ShortcutConfiguration CaptureConfiguration()
    {
        var configuration = new ShortcutConfiguration();
        foreach (ShortcutDefinition definition in _definitions)
        {
            configuration.Shortcuts.Add(new ShortcutConfiguration.ShortcutEntry
            {
                ActionId = definition.ActionId,
                Gesture = definition.Gesture?.ToDisplayText(),
                IsEnabled = definition.IsEnabled,
            });
        }

        return configuration;
    }

    private void ApplyConfiguration(ShortcutConfiguration configuration)
    {
        foreach (ShortcutConfiguration.ShortcutEntry entry in configuration.Shortcuts)
        {
            if (entry.ActionId is null || !_definitionsById.TryGetValue(entry.ActionId, out ShortcutDefinition? definition))
            {
                continue;
            }

            definition.Gesture = ShortcutKeyGesture.Parse(entry.Gesture);
            definition.IsEnabled = entry.IsEnabled ?? true;
        }
    }

    private void RebuildConflicts()
    {
        var groups = _definitions
            .Where(definition => definition.IsEnabled && definition.Gesture is not null)
            .GroupBy(definition => definition.Gesture!.ToDisplayText(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (ShortcutDefinition definition in _definitions)
        {
            definition.HasConflict = false;
            definition.ConflictMessage = string.Empty;
        }

        foreach (IGrouping<string, ShortcutDefinition> group in groups)
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            string[] actionIds = group.Select(definition => definition.ActionId).ToArray();
            foreach (ShortcutDefinition definition in group)
            {
                definition.HasConflict = true;
                definition.ConflictMessage = $"Shortcut.ConflictWith:{string.Join(",", actionIds.Except([definition.ActionId]))}";
            }
        }
    }
}
