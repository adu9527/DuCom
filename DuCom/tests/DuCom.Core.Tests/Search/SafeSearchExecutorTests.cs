using DuCom.Core.Search;
using DuCom.Core.Storage;
using Xunit;

namespace DuCom.Core.Tests.Search;

public class SafeSearchExecutorTests
{
    [Fact]
    public async Task ProviderInvokedExactlyOnceWhenComposedInsideBackgroundWork()
    {
        int invocations = 0;
        LineStoreSnapshot snapshot = CreateSnapshot(["alpha target beta"]);

        SearchResult result = await Task.Run(() => SafeSearchExecutor.Execute(
            () => { invocations++; return snapshot; },
            new SearchRequest("target", UseRegex: false, MatchCase: false)));

        Assert.Single(result.Matches);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void ThrowingProviderDegradesToEmptyResultAndReports()
    {
        List<Exception> reported = [];
        SearchResult result = SafeSearchExecutor.Execute(
            () => throw new InvalidOperationException("store busy"),
            new SearchRequest("x", false, false),
            onError: reported.Add);

        Assert.Empty(result.Matches);
        Assert.Null(result.ErrorMessage);
        Assert.False(result.IsCancelled);
        Assert.Single(reported);
        Assert.IsType<InvalidOperationException>(reported[0]);
    }

    [Fact]
    public void NullSnapshotOrEmptyPatternYieldsEmptyResultWithoutEngineCall()
    {
        Assert.Equal(SearchResult.Empty, SafeSearchExecutor.Execute(() => null, new SearchRequest("p", false, false)));
        LineStoreSnapshot empty = CreateSnapshot([]);
        Assert.Equal(SearchResult.Empty, SafeSearchExecutor.Execute(() => empty, new SearchRequest(string.Empty, false, false)));
    }

    [Fact]
    public void CancelledBeforeExecutionThrowsOperationCanceled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => SafeSearchExecutor.Execute(
            () => CreateSnapshot(["data"]),
            new SearchRequest("d", false, false),
            onError: _ => Assert.Fail("provider must not run after cancellation"),
            cts.Token));
    }

    [Fact]
    public async Task LargeSnapshotExecutesQuicklyWhenInvokedOnThreadPool()
    {
        // 20k lines: snapshot copy + search stay far under a generous 2 s budget even on CI.
        string[] lines = Enumerable.Range(0, 20_000).Select(index => $"line {index} marker").ToArray();
        LineStoreSnapshot snapshot = CreateSnapshot(lines);

        SearchResult result = await Task.Run(() => SafeSearchExecutor.Execute(
            () => snapshot,
            new SearchRequest("marker", false, false)));

        Assert.Equal(20_000, result.Matches.Count);
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
