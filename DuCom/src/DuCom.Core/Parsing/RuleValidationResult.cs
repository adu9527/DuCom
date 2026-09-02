namespace DuCom.Core.Parsing;

public readonly record struct RuleValidationResult(bool IsValid, string? ErrorKey);
