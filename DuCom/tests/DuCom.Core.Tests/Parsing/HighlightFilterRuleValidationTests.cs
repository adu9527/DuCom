using DuCom.Core.Parsing;

namespace DuCom.Core.Tests.Parsing;

public sealed class HighlightFilterRuleValidationTests
{
    [Fact]
    public void Validate_ValidHighlight_ReturnsValid()
    {
        HighlightFilterRule rule = new(
            Guid.NewGuid(),
            "Errors",
            HighlightFilterRuleKind.Highlight,
            RuleMatchMode.Regex,
            "error",
            false,
            true,
            0xFF,
            0x00,
            0x00,
            null,
            null,
            null);

        RuleValidationResult result = HighlightFilterRuleValidation.Validate(rule);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorKey);
    }

    [Fact]
    public void Validate_ValidFilterWithoutColor_ReturnsValid()
    {
        HighlightFilterRule rule = new(
            Guid.NewGuid(),
            "Errors",
            HighlightFilterRuleKind.Filter,
            RuleMatchMode.Contains,
            "error",
            false,
            true,
            null,
            null,
            null,
            null,
            null,
            null);

        RuleValidationResult result = HighlightFilterRuleValidation.Validate(rule);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_EmptyName_ReturnsInvalidName()
    {
        HighlightFilterRule rule = CreateMinimalHighlight("error", RuleMatchMode.Regex) with { Name = "   " };

        RuleValidationResult result = HighlightFilterRuleValidation.Validate(rule);

        Assert.False(result.IsValid);
        Assert.Equal(HighlightFilterRuleValidation.InvalidNameKey, result.ErrorKey);
    }

    [Fact]
    public void Validate_EmptyPattern_ReturnsEmptyPattern()
    {
        HighlightFilterRule rule = CreateMinimalHighlight("", RuleMatchMode.Contains);

        RuleValidationResult result = HighlightFilterRuleValidation.Validate(rule);

        Assert.False(result.IsValid);
        Assert.Equal(HighlightFilterRuleValidation.EmptyPatternKey, result.ErrorKey);
    }

    [Fact]
    public void Validate_InvalidRegex_ReturnsInvalidRegex()
    {
        HighlightFilterRule rule = CreateMinimalHighlight("[invalid", RuleMatchMode.Regex);

        RuleValidationResult result = HighlightFilterRuleValidation.Validate(rule);

        Assert.False(result.IsValid);
        Assert.Equal(HighlightFilterRuleValidation.InvalidRegexKey, result.ErrorKey);
    }

    [Fact]
    public void Validate_HighlightWithoutColor_ReturnsMissingColor()
    {
        HighlightFilterRule rule = new(
            Guid.NewGuid(),
            "NoColor",
            HighlightFilterRuleKind.Highlight,
            RuleMatchMode.Contains,
            "text",
            false,
            true,
            null,
            null,
            null,
            null,
            null,
            null);

        RuleValidationResult result = HighlightFilterRuleValidation.Validate(rule);

        Assert.False(result.IsValid);
        Assert.Equal(HighlightFilterRuleValidation.MissingColorKey, result.ErrorKey);
    }

    [Fact]
    public void Validate_HighlightWithBackgroundOnly_ReturnsValid()
    {
        HighlightFilterRule rule = new(
            Guid.NewGuid(),
            "BgOnly",
            HighlightFilterRuleKind.Highlight,
            RuleMatchMode.Contains,
            "text",
            false,
            true,
            null,
            null,
            null,
            0x00,
            0x00,
            0x00);

        RuleValidationResult result = HighlightFilterRuleValidation.Validate(rule);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_InvalidRegexForContains_DoesNotValidateRegex()
    {
        HighlightFilterRule rule = CreateMinimalHighlight("[invalid", RuleMatchMode.Contains);

        RuleValidationResult result = HighlightFilterRuleValidation.Validate(rule);

        Assert.True(result.IsValid);
    }

    private static HighlightFilterRule CreateMinimalHighlight(string pattern, RuleMatchMode mode)
    {
        return new HighlightFilterRule(
            Guid.NewGuid(),
            "Test",
            HighlightFilterRuleKind.Highlight,
            mode,
            pattern,
            false,
            true,
            0xFF,
            0xFF,
            0xFF,
            null,
            null,
            null);
    }
}
