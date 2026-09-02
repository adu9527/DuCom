namespace DuCom.Services.Shortcuts;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}
