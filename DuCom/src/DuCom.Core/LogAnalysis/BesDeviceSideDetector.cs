using System.Text.RegularExpressions;

namespace DuCom.Core.LogAnalysis;

public static partial class BesDeviceSideDetector
{
    public static string? Detect(string text)
    {
        Match match = ExplicitSideRegex().Match(text);
        if (!match.Success) match = NamedEarRegex().Match(text);
        if (!match.Success) return null;
        string value = match.Groups["side"].Value;
        return value.Equals("left", StringComparison.OrdinalIgnoreCase) || value == "左" ? "左" : "右";
    }

    [GeneratedRegex(@"(?i)\b(?:ear[_ -]?side|local[_ -]?ear|bud[_ -]?side|device[_ -]?side)\s*[:=]\s*(?<side>left|right)\b|(?:耳机侧|设备侧)\s*[:=：]\s*(?<side>左|右)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ExplicitSideRegex();

    [GeneratedRegex(@"(?i)\b(?:(?<side>left|right)[_ -]?(?:ear|earbud|bud)|(?:ear|earbud|bud)[_ -]?(?<side>left|right))\b|(?<side>左|右)耳(?:机)?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex NamedEarRegex();
}
