namespace DuCom.Services;

/// <summary>Single-flight, rate-limited metadata; presence discovery never waits for WMI.</summary>
internal sealed class PortMetadataCache(
    Func<Task<IReadOnlyDictionary<string, DiscoveredPort>>> queryAsync,
    Func<DateTimeOffset> utcNow,
    Action<Exception> logException)
{
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MissingMetadataInterval = TimeSpan.FromSeconds(30);
    private readonly object _sync = new();
    private IReadOnlyDictionary<string, DiscoveredPort> _cached = new Dictionary<string, DiscoveredPort>(StringComparer.OrdinalIgnoreCase);
    private Task<IReadOnlyDictionary<string, DiscoveredPort>>? _pending;
    private string[] _queriedNames = [];
    private string[] _lastPresentNames = [];
    private readonly HashSet<string> _invalidatedNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? _lastAttempt;

    internal IReadOnlyDictionary<string, DiscoveredPort> Get(IReadOnlyList<string> presentNames)
    {
        lock (_sync)
        {
            // A removed/re-added COM name is a new generation. Do not publish the
            // old device identity, including from a WMI query started before removal.
            foreach (string removed in _lastPresentNames.Except(presentNames, StringComparer.OrdinalIgnoreCase))
            {
                _invalidatedNames.Add(removed);
            }
            _lastPresentNames = [.. presentNames];

            if (_pending is { IsCompleted: true })
            {
                try
                {
                    _cached = _pending.GetAwaiter().GetResult()
                        .Where(pair => !_invalidatedNames.Contains(pair.Key)
                            && _queriedNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
                            && presentNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception exception)
                {
                    logException(exception);
                    _queriedNames = []; // Retry missing metadata after the cooldown.
                }
                _pending = null;
            }

            if (_cached.Keys.Any(_invalidatedNames.Contains))
            {
                _cached = _cached.Where(pair => !_invalidatedNames.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            }

            DateTimeOffset now = utcNow();
            TimeSpan elapsed = _lastAttempt is { } last ? now - last : TimeSpan.MaxValue;
            bool namesChanged = !_queriedNames.SequenceEqual(presentNames, StringComparer.OrdinalIgnoreCase);
            bool metadataMissing = presentNames.Any(name => !_cached.ContainsKey(name));
            bool invalidatedPresent = presentNames.Any(_invalidatedNames.Contains);
            if (presentNames.Count > 0 && _pending is null && elapsed >= RetryInterval
                && (_lastAttempt is null || namesChanged || invalidatedPresent || (metadataMissing && elapsed >= MissingMetadataInterval) || elapsed >= RefreshInterval))
            {
                _queriedNames = [.. presentNames];
                _invalidatedNames.Clear(); // Only this new query may identify the new generation.
                _lastAttempt = now;
                try
                {
                    _pending = queryAsync();
                }
                catch (Exception exception)
                {
                    logException(exception);
                    _queriedNames = [];
                }
            }

            return _cached;
        }
    }
}
