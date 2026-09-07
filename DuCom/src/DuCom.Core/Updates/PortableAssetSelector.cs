namespace DuCom.Core.Updates;

/// <summary>Selects the portable executable asset from a GitHub release.</summary>
public static class PortableAssetSelector
{
    private static readonly string[] ExcludedMarkers =
    [
        "setup",
        "installer",
        "install",
        "symbols",
        ".pdb",
        "-delta-",
        "-full-",
        "uninstaller",
    ];

    public static GitHubReleaseAsset? Select(IEnumerable<GitHubReleaseAsset> assets, string preferredName = "DuCom.exe")
    {
        ArgumentNullException.ThrowIfNull(assets);

        List<GitHubReleaseAsset> candidates = [.. assets.Where(asset =>
            !string.IsNullOrWhiteSpace(asset.Name) &&
            !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl) &&
            !string.Equals(asset.State, "starter", StringComparison.OrdinalIgnoreCase))];

        GitHubReleaseAsset? exact = candidates.FirstOrDefault(asset =>
            string.Equals(asset.Name, preferredName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        List<GitHubReleaseAsset> executables = [.. candidates
            .Where(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(asset => !ContainsExcludedMarker(asset.Name))];

        return executables.Count switch
        {
            1 => executables[0],
            0 => null,
            _ => executables.FirstOrDefault(asset => asset.Name.Contains("portable", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static bool ContainsExcludedMarker(string name)
    {
        foreach (string marker in ExcludedMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
