namespace DuCom.Core.Search;

public sealed record SearchRequest(string Pattern, bool UseRegex, bool MatchCase, bool MatchWholeLine = false);
