using DuCom.Core.Storage;

namespace DuCom.Core.Search;

/// <summary>
/// Exception-safe wrapper around snapshot acquisition plus one search pass, intended to be
/// invoked from background threads so the UI thread never performs a full line-store copy.
/// A failing or null snapshot provider degrades to an empty result instead of throwing.
/// </summary>
public static class SafeSearchExecutor
{
    public static SearchResult Execute(
        Func<LineStoreSnapshot?> snapshotProvider,
        SearchRequest request,
        Action<Exception>? onError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotProvider);
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        LineStoreSnapshot? snapshot;
        try
        {
            snapshot = snapshotProvider();
        }
        catch (Exception exception)
        {
            onError?.Invoke(exception);
            return SearchResult.Empty;
        }

        if (snapshot is null || string.IsNullOrEmpty(request.Pattern))
        {
            return SearchResult.Empty;
        }

        return LogSearchEngine.Search(snapshot, request, cancellationToken);
    }
}
