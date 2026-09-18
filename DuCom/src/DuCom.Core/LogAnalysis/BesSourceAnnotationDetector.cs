using System.Text.RegularExpressions;

namespace DuCom.Core.LogAnalysis;

public sealed record BesSourceAnnotation(string? Side, string? TwsRole)
{
    public string Display => string.Join("，", new[] { Side, TwsRole }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public static partial class BesSourceAnnotationDetector
{
    public static BesSourceAnnotation Detect(string text)
    {
        string? side = BesDeviceSideDetector.Detect(text);
        Match roleMatch = ExplicitTwsRoleRegex().Match(text);
        string? role = roleMatch.Success
            ? roleMatch.Groups["role"].Value.Equals("master", StringComparison.OrdinalIgnoreCase) || roleMatch.Groups["role"].Value == "主"
                ? "当前主机"
                : "当前从机"
            : null;
        return new BesSourceAnnotation(side, role);
    }

    [GeneratedRegex(@"(?i)\b(?:tws[_ -]?role|ibrt[_ -]?role|current[_ -]?role)\s*[:=]\s*(?<role>master|slave)\b|(?:TWS|IBRT)?\s*(?:当前)?角色\s*[:=：]\s*(?<role>主|从)(?:机|耳)?", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ExplicitTwsRoleRegex();
}
