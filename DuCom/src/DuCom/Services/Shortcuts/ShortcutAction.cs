namespace DuCom.Services.Shortcuts;

public sealed record ShortcutAction(string ActionId, string DisplayNameKey, string DefaultGesture, string CategoryKey);
