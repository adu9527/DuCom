namespace DuCom.Services.Shortcuts;

public sealed class ShortcutConfiguration
{
    public int Version { get; set; } = 1;

    public List<ShortcutEntry> Shortcuts { get; set; } = [];

    public sealed class ShortcutEntry
    {
        public string ActionId { get; set; } = string.Empty;

        public string? Gesture { get; set; }

        public bool? IsEnabled { get; set; }
    }
}
