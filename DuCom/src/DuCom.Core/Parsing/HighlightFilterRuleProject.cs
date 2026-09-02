namespace DuCom.Core.Parsing;

/// <summary>A named highlight rules file containing multiple independently styled entries.</summary>
public sealed record HighlightFilterRuleProject(Guid Id, string Name, IReadOnlyList<HighlightFilterRule> Rules);
