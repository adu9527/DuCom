namespace DuCom.Core.Persistence;

/// <summary>
/// Pure helpers for the persisted hidden-port list (settings snapshot). Keeping the
/// normalization/merge here makes the "hidden ports survive restart" contract testable in
/// Core: the application layer persists the exact list produced here and restores it with
/// the same normalizer on load.
/// </summary>
public static class PortVisibility
{
    /// <summary>Trims, drops empties, deduplicates (case-insensitive), keeps first-seen order.</summary>
    public static IReadOnlyList<string> NormalizeHidden(IEnumerable<string?>? candidates)
    {
        if (candidates is null)
        {
            return [];
        }

        List<string> result = [];
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string name = candidate.Trim();
            if (!result.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>Union of the currently hidden ports and imported ones (normalized, ordered current-first).</summary>
    public static IReadOnlyList<string> MergeHidden(IEnumerable<string> current, IEnumerable<string> imported) =>
        NormalizeHidden([.. current, .. imported]);
}
