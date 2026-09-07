using DuCom.Core.Parsing;
using DuCom.Core.Sending;

namespace DuCom.Services;

/// <summary>
/// Built-in starter data for first launches. Personal working data such as private
/// command groups must never be added here because it ships inside the executable.
/// </summary>
public static class DefaultDuComData
{
    public const string MyProjectName = "我的项目";

    public static IReadOnlyList<CommandGroup> MergeCommandGroups(
        IReadOnlyList<CommandGroup> groups,
        out bool changed)
    {
        List<CommandGroup> merged = [.. groups];
        changed = false;
        foreach (CommandGroup group in CreateCommandGroups())
        {
            if (merged.Any(existing => string.Equals(existing.Name, group.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            merged.Add(group);
            changed = true;
        }

        return merged;
    }

    public static IReadOnlyList<HighlightFilterRule> MergeHighlightRules(
        IReadOnlyList<HighlightFilterRule> rules,
        out bool changed)
    {
        List<HighlightFilterRule> merged = [.. rules];
        changed = false;
        foreach (HighlightFilterRule rule in CreateHighlightRules())
        {
            if (merged.Any(existing => string.Equals(existing.Name, rule.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            merged.Add(rule);
            changed = true;
        }

        return merged;
    }

    private static IReadOnlyList<CommandGroup> CreateCommandGroups() =>
    [
        // The starter project intentionally starts empty and remains the default selection.
        CommandGroup.Create(MyProjectName),
    ];

    private static IReadOnlyList<HighlightFilterRule> CreateHighlightRules() =>
    [
        Highlight("BES / Error", @"\b(?:ERROR|ERR|FATAL|ASSERT|PANIC|EXCEPTION|FAIL|FAILED|bybye|shutdown)\b", 255, 85, 85, true),
        Highlight("BES / Warning", @"\b(?:WARN|WARNING|UNDERRUN|OVERRUN)\b", 255, 215, 0, true),
        Highlight("BES / Status", @"\b(?:INFO|DEBUG|TRACE|RUNNING|SUCCESS|CONNECTED|DISCONNECTED)\b", 102, 204, 255),
        Highlight("BES / Fault", @"\b(?:WATCHDOG|CRASH|FAULT|HARDFAULT|STACK_OVERFLOW|HEAP_OVERFLOW|PLUGOUT)\b", 255, 140, 0, true),
        Highlight("BES / Audio", @"\b(?:ANC|CODEC|SBC|AAC|LDAC|LHDC|LC3|A2DP|SCO)\b", 124, 252, 0, true),
        Highlight("BES / Address", @"0x[0-9A-Fa-f]+", 0, 206, 209),
        Highlight("BES / Unit", @"\b\d+(?:\.\d+)?\s*(?:ms|us|Hz|kHz|MHz|dB|dBm|mV|mA|KB|MB|%)\b", 152, 251, 152),
        Highlight("BES / Version", "BES_v2", 51, 51, 51),
    ];

    private static HighlightFilterRule Highlight(string name, string pattern, byte red, byte green, byte blue, bool bold = false) =>
        new(Guid.NewGuid(), name, HighlightFilterRuleKind.Highlight, RuleMatchMode.Regex, pattern, false, true,
            red, green, blue, null, null, null, bold);
}
