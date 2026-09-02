using System.Collections.ObjectModel;
using DuCom.Core.Search;
using DuCom.Core.Storage;

namespace DuCom.Core.Tests.Search;

public sealed class LogSearchEngineTests
{
    [Fact]
    public void EmptyPatternReturnsEmptyResult()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["hello", "world"]);
        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest(string.Empty, UseRegex: false, MatchCase: false));

        Assert.Empty(result.Matches);
        Assert.Null(result.ErrorMessage);
        Assert.False(result.IsCancelled);
    }

    [Theory]
    [InlineData("hello", false)]
    [InlineData("HELLO", false)]
    [InlineData("Hello", true)]
    public void TextSearchRespectsCaseSensitivity(string pattern, bool expectedSensitiveFound)
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["Hello World"]);
        SearchResult sensitive = LogSearchEngine.Search(snapshot, new SearchRequest(pattern, UseRegex: false, MatchCase: true));
        SearchResult insensitive = LogSearchEngine.Search(snapshot, new SearchRequest(pattern, UseRegex: false, MatchCase: false));

        Assert.Equal(expectedSensitiveFound ? 1 : 0, sensitive.Matches.Count);
        Assert.Single(insensitive.Matches);
    }

    [Fact]
    public void TextSearchFindsMultipleNonOverlappingMatches()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["abc abc abc"]);
        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest("abc", UseRegex: false, MatchCase: false));

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(0, result.Matches[0].StartIndex);
        Assert.Equal(4, result.Matches[1].StartIndex);
        Assert.Equal(8, result.Matches[2].StartIndex);
    }

    [Fact]
    public void TextSearchDoesNotFindMatchesAcrossSegments()
    {
        LineStoreSnapshot snapshot = new(
            FirstLogicalId: 1,
            LastLogicalId: 2,
            EvictedLineCount: 0,
            new List<StoredLine>
            {
                new(1, 0, LineDirection.Rx, DateTimeOffset.UtcNow, "abc", true),
                new(2, 0, LineDirection.Rx, DateTimeOffset.UtcNow, "def", true),
            }.AsReadOnly());

        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest("abcdef", UseRegex: false, MatchCase: false));

        Assert.Empty(result.Matches);
    }

    [Fact]
    public void RegexSearchFindsPatternAndGroupsDoNotCreateExtraMatches()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["error: 123", "info: 456", "error: 789"]);
        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest(@"error:\s*(\d+)", UseRegex: true, MatchCase: false));

        Assert.Equal(2, result.Matches.Count);
        Assert.All(result.Matches, match => Assert.True(match.Length > 0));
    }

    [Fact]
    public void RegexSearchIsCaseSensitiveWhenRequested()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["ERROR: 1", "error: 2"]);
        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest(@"ERROR", UseRegex: true, MatchCase: true));

        Assert.Single(result.Matches);
        Assert.Equal(1, result.Matches[0].LogicalId);
    }

    [Fact]
    public void RegexErrorReturnsErrorMessage()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["any text"]);
        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest("[invalid", UseRegex: true, MatchCase: false));

        Assert.Empty(result.Matches);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public void CancellationReturnsCancelledResult()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(Enumerable.Repeat("some log line content", 100).ToArray());
        using CancellationTokenSource source = new();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LogSearchEngine.Search(snapshot, new SearchRequest("content", UseRegex: false, MatchCase: false), source.Token));
    }

    [Fact]
    public void MatchesPreserveLogicalIdAndSegmentIndex()
    {
        LineStoreSnapshot snapshot = new(
            FirstLogicalId: 1,
            LastLogicalId: 2,
            EvictedLineCount: 0,
            new List<StoredLine>
            {
                new(1, 0, LineDirection.Rx, DateTimeOffset.UtcNow, "first", true),
                new(1, 1, LineDirection.Rx, DateTimeOffset.UtcNow, "second", true),
                new(2, 0, LineDirection.Rx, DateTimeOffset.UtcNow, "first again", true),
            }.AsReadOnly());

        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest("first", UseRegex: false, MatchCase: false));

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(1, result.Matches[0].LogicalId);
        Assert.Equal(0, result.Matches[0].SegmentIndex);
        Assert.Equal(2, result.Matches[1].LogicalId);
        Assert.Equal(0, result.Matches[1].SegmentIndex);
    }

    [Fact]
    public void TextSearchUsesOrdinalComparison()
    {
        LineStoreSnapshot snapshot = CreateSnapshot([" café "]);
        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest("café", UseRegex: false, MatchCase: false));

        Assert.Single(result.Matches);
    }

    [Fact]
    public void WholeLineTextSearchMatchesOnlyCompleteLine()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["abc", "abc def", "ABC"]);
        SearchResult insensitive = LogSearchEngine.Search(snapshot, new SearchRequest("abc", UseRegex: false, MatchCase: false, MatchWholeLine: true));
        SearchResult sensitive = LogSearchEngine.Search(snapshot, new SearchRequest("abc", UseRegex: false, MatchCase: true, MatchWholeLine: true));

        Assert.Equal([1L, 3L], insensitive.Matches.Select(match => match.LogicalId));
        Assert.Equal([1L], sensitive.Matches.Select(match => match.LogicalId));
    }

    private static LineStoreSnapshot CreateSnapshot(string[] lines)
    {
        List<StoredLine> storedLines = new(lines.Length);
        for (int index = 0; index < lines.Length; index++)
        {
            storedLines.Add(new StoredLine(
                index + 1,
                0,
                LineDirection.Rx,
                DateTimeOffset.UtcNow,
                lines[index],
                true));
        }

        return new LineStoreSnapshot(
            storedLines.Count == 0 ? null : 1,
            storedLines.Count == 0 ? null : storedLines.Count,
            0,
            storedLines.AsReadOnly());
    }
}
