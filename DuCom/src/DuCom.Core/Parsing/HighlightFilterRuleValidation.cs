using System.Text.RegularExpressions;

namespace DuCom.Core.Parsing;

public static class HighlightFilterRuleValidation
{
    public const string InvalidNameKey = "HighlightFilter.Error.InvalidName";
    public const string EmptyPatternKey = "HighlightFilter.Error.EmptyPattern";
    public const string InvalidRegexKey = "HighlightFilter.Error.InvalidRegex";
    public const string MissingColorKey = "HighlightFilter.Error.MissingColor";

    public static RuleValidationResult Validate(HighlightFilterRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            return new RuleValidationResult(false, InvalidNameKey);
        }

        if (string.IsNullOrEmpty(rule.Pattern))
        {
            return new RuleValidationResult(false, EmptyPatternKey);
        }

        if (rule.Kind == HighlightFilterRuleKind.Highlight && !rule.HasForeground && !rule.HasBackground)
        {
            return new RuleValidationResult(false, MissingColorKey);
        }

        if (rule.Mode == RuleMatchMode.Regex)
        {
            try
            {
                _ = Regex.Match(string.Empty, rule.Pattern, RegexOptions.None, HighlightFilterRuleMatcher.MatchTimeout);
            }
            catch
            {
                return new RuleValidationResult(false, InvalidRegexKey);
            }
        }

        return new RuleValidationResult(true, null);
    }
}
