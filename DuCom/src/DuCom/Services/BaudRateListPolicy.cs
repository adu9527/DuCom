namespace DuCom.Services;

/// <summary>
/// Pure baud-rate list rules for the port picker: the list stays sorted ascending,
/// never empty, and never drops a rate that is currently in use. Extracted from
/// MainViewModel so the rules are unit-testable without a Dispatcher; the view model
/// applies returned sequences to its observable collection.
/// </summary>
public static class BaudRateListPolicy
{
    public static IReadOnlyList<int> DefaultBaudRates { get; } =
        [9_600, 19_200, 115_200, 921_600, 1_152_000, 1_500_000, 2_000_000, 3_000_000];

    /// <returns>The ordered list with <paramref name="value"/> added, or null when the
    /// value is invalid (non-positive) or already present — both mean "no change".</returns>
    public static IReadOnlyList<int>? Add(IReadOnlyList<int> existing, int value)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (value <= 0 || existing.Contains(value))
        {
            return null;
        }

        return [.. existing.Append(value).Order()];
    }

    /// <summary>Returns the ordered list guaranteed to contain <paramref name="value"/>;
    /// non-positive values are ignored.</summary>
    public static IReadOnlyList<int> EnsurePresent(IReadOnlyList<int> existing, int value)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (value <= 0 || existing.Contains(value))
        {
            return [.. existing.Order()];
        }

        return [.. existing.Append(value).Order()];
    }

    /// <summary>
    /// Restores the default rates while protecting the ones in active use: the result is
    /// the union of the defaults and <paramref name="inUseRates"/>, ordered ascending.
    /// It is never empty because the defaults are non-empty.
    /// </summary>
    public static IReadOnlyList<int> PruneToDefaults(IEnumerable<int> inUseRates)
    {
        ArgumentNullException.ThrowIfNull(inUseRates);
        return [.. DefaultBaudRates.Concat(inUseRates).Distinct().Order()];
    }
}
