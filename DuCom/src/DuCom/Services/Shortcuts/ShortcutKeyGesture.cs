using System.Text;

namespace DuCom.Services.Shortcuts;

public sealed record ShortcutKeyGesture(string KeyName, ShortcutModifiers Modifiers)
{
    private static readonly HashSet<string> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl",
        "Control",
        "Alt",
        "Shift",
        "Win",
        "Windows",
        "Meta",
    };

    public string NormalizedKey => NormalizeKeyName(KeyName);

    public bool IsEmpty => string.IsNullOrWhiteSpace(KeyName);

    public bool IsModifierOnly => !IsEmpty && ModifierNames.Contains(NormalizedKey);

    public string ToDisplayText()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        AppendModifier(builder, ShortcutModifiers.Ctrl, "Ctrl");
        AppendModifier(builder, ShortcutModifiers.Shift, "Shift");
        AppendModifier(builder, ShortcutModifiers.Alt, "Alt");
        AppendModifier(builder, ShortcutModifiers.Win, "Win");
        if (builder.Length > 0)
        {
            builder.Append('+');
        }

        builder.Append(NormalizedKey);
        return builder.ToString();
    }

    public static ShortcutKeyGesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ShortcutModifiers modifiers = ShortcutModifiers.None;
        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        for (int index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= ParseModifier(parts[index].Trim());
        }

        string key = parts[^1].Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return new ShortcutKeyGesture(key, modifiers);
    }

    public static bool TryParse(string? text, out ShortcutKeyGesture? gesture)
    {
        gesture = Parse(text);
        return gesture is not null;
    }

    public bool Matches(ShortcutKeyGesture other) =>
        Modifiers == other.Modifiers &&
        string.Equals(NormalizedKey, other.NormalizedKey, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => ToDisplayText();

    private static string NormalizeKeyName(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        if (trimmed.Length == 1)
        {
            return trimmed.ToUpperInvariant();
        }

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static ShortcutModifiers ParseModifier(string text) =>
        text switch
        {
            _ when text.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) => ShortcutModifiers.Ctrl,
            _ when text.Equals("Control", StringComparison.OrdinalIgnoreCase) => ShortcutModifiers.Ctrl,
            _ when text.Equals("Alt", StringComparison.OrdinalIgnoreCase) => ShortcutModifiers.Alt,
            _ when text.Equals("Shift", StringComparison.OrdinalIgnoreCase) => ShortcutModifiers.Shift,
            _ when text.Equals("Win", StringComparison.OrdinalIgnoreCase) => ShortcutModifiers.Win,
            _ when text.Equals("Windows", StringComparison.OrdinalIgnoreCase) => ShortcutModifiers.Win,
            _ when text.Equals("Meta", StringComparison.OrdinalIgnoreCase) => ShortcutModifiers.Win,
            _ => ShortcutModifiers.None,
        };

    private void AppendModifier(StringBuilder builder, ShortcutModifiers modifier, string label)
    {
        if ((Modifiers & modifier) != modifier)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('+');
        }

        builder.Append(label);
    }
}
