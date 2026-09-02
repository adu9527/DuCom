namespace DuCom.Core.Search;

public sealed record SearchResult(IReadOnlyList<SearchMatch> Matches, string? ErrorMessage, bool IsCancelled)
{
    public static SearchResult Empty { get; } = new([], null, false);
}
