namespace DuCom.Services.Shortcuts;

public sealed class ShortcutDefinition
{
    public ShortcutDefinition(ShortcutAction action, string displayName)
    {
        Action = action;
        DisplayName = displayName;
    }

    public ShortcutAction Action { get; }

    public string ActionId => Action.ActionId;

    public string DisplayName { get; set; }

    public ShortcutKeyGesture? Gesture { get; set; }

    public bool IsEnabled { get; set; } = true;

    public ShortcutKeyGesture? DefaultGesture => ShortcutKeyGesture.Parse(Action.DefaultGesture);

    public bool HasConflict { get; set; }

    public string ConflictMessage { get; set; } = string.Empty;

    public string GestureText => Gesture?.ToDisplayText() ?? string.Empty;

    public string DefaultGestureText => DefaultGesture?.ToDisplayText() ?? string.Empty;

    public void ResetToDefault()
    {
        Gesture = DefaultGesture;
        IsEnabled = true;
    }
}
