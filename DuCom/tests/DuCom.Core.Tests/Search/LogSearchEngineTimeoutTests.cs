using DuCom.Core.Search;
using DuCom.Core.Storage;

namespace DuCom.Core.Tests.Search;

public sealed class LogSearchEngineTimeoutTests
{
    // Named group keeps capture semantics under ExplicitCapture, defeating the .NET regex
    // analyzer's atomic rewrite so the catastrophic backtracking is preserved.
    private const string CatastrophicPattern = @"(?<x>a+)+b";

    private static string CatastrophicInput(int length) => new string('a', length) + "c";

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    public async Task CatastrophicRegex_ReturnsTimeoutMarkerWithoutThrowing(int inputLength)
    {
        LineStoreSnapshot snapshot = CreateSnapshot([CatastrophicInput(inputLength)]);

        SearchResult result = await Task.Run(() =>
            LogSearchEngine.Search(snapshot, new SearchRequest(CatastrophicPattern, UseRegex: true, MatchCase: false)));

        Assert.Empty(result.Matches);
        Assert.Equal(LogSearchEngine.RegexTimeoutMessage, result.ErrorMessage);
        Assert.False(result.IsCancelled);
    }

    [Fact]
    public async Task CatastrophicRegexThroughSafeExecutor_TaskDoesNotFault()
    {
        LineStoreSnapshot snapshot = CreateSnapshot([CatastrophicInput(40)]);

        SearchResult result = await Task.Run(() =>
            SafeSearchExecutor.Execute(
                () => snapshot,
                new SearchRequest(CatastrophicPattern, UseRegex: true, MatchCase: false)));

        Assert.Equal(LogSearchEngine.RegexTimeoutMessage, result.ErrorMessage);
    }

    [Fact]
    public void AfterRegexTimeout_PlainTextSearchStillWorks()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["error: something failed", CatastrophicInput(40)]);
        SearchResult timedOut = LogSearchEngine.Search(
            snapshot,
            new SearchRequest(CatastrophicPattern, UseRegex: true, MatchCase: false));

        SearchResult textResult = LogSearchEngine.Search(
            snapshot,
            new SearchRequest("something", UseRegex: false, MatchCase: false));

        Assert.Equal(LogSearchEngine.RegexTimeoutMessage, timedOut.ErrorMessage);
        Assert.Single(textResult.Matches);
        Assert.Null(textResult.ErrorMessage);
    }

    [Fact]
    public void AfterRegexTimeout_HealthyRegexSearchStillWorks()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["error: " + CatastrophicInput(40)]);
        LogSearchEngine.Search(snapshot, new SearchRequest(CatastrophicPattern, UseRegex: true, MatchCase: false));

        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest(@"error:", UseRegex: true, MatchCase: false));

        Assert.Null(result.ErrorMessage);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void LongInputWithNormalRegex_CompletesWithoutTimeout()
    {
        string longLine = string.Join(" ", Enumerable.Repeat("entry=42", 2_000));
        LineStoreSnapshot snapshot = CreateSnapshot([longLine]);

        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest(@"\d+", UseRegex: true, MatchCase: false));

        Assert.Null(result.ErrorMessage);
        Assert.Equal(2_000, result.Matches.Count);
    }

    [Fact]
    public void InvalidRegex_StillReturnsErrorMessage()
    {
        LineStoreSnapshot snapshot = CreateSnapshot(["text"]);

        SearchResult result = LogSearchEngine.Search(snapshot, new SearchRequest("[invalid", UseRegex: true, MatchCase: false));

        Assert.NotNull(result.ErrorMessage);
        Assert.NotEqual(LogSearchEngine.RegexTimeoutMessage, result.ErrorMessage);
    }

    [Fact]
    public void MatchTimeout_IsHundredMilliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(100), LogSearchEngine.MatchTimeout);
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
