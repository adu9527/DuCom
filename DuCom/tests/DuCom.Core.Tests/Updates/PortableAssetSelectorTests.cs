using DuCom.Core.Updates;

namespace DuCom.Core.Tests.Updates;

public sealed class PortableAssetSelectorTests
{
    private static GitHubReleaseAsset Asset(string name, string url = "https://example.com/download") =>
        new(name, $"{url}/{name}", 1024);

    [Fact]
    public void ExactPortableAssetNameWins()
    {
        GitHubReleaseAsset? selected = PortableAssetSelector.Select(
        [
            Asset("DuCom-win-Setup.exe"),
            Asset("DuCom.exe"),
            Asset("DuCom-win-full-0.0.3.nupkg"),
        ]);

        Assert.NotNull(selected);
        Assert.Equal("DuCom.exe", selected.Name);
    }

    [Fact]
    public void SingleUnambiguousExecutableIsSelected()
    {
        GitHubReleaseAsset? selected = PortableAssetSelector.Select(
        [
            Asset("DuCom-win-full-0.0.3.nupkg"),
            Asset("DuCom-V0.0.0.3.exe"),
        ]);

        Assert.NotNull(selected);
        Assert.Equal("DuCom-V0.0.0.3.exe", selected.Name);
    }

    [Fact]
    public void InstallerAndSymbolAssetsAreIgnored()
    {
        GitHubReleaseAsset? selected = PortableAssetSelector.Select(
        [
            Asset("DuCom-win-Setup.exe"),
            Asset("DuCom.pdb"),
        ]);

        Assert.Null(selected);
    }

    [Fact]
    public void MultipleExecutablesFallBackToPortableMarker()
    {
        GitHubReleaseAsset? selected = PortableAssetSelector.Select(
        [
            Asset("DuCom-win-Setup.exe"),
            Asset("DuCom-portable.exe"),
        ]);

        Assert.NotNull(selected);
        Assert.Equal("DuCom-portable.exe", selected.Name);
    }

    [Fact]
    public void AssetsWithoutDownloadUrlAreIgnored()
    {
        GitHubReleaseAsset empty = new("DuCom.exe", "", 1024);
        GitHubReleaseAsset pending = new("DuCom-other.exe", "https://example.com/other", 1024, State: "starter");

        GitHubReleaseAsset? selected = PortableAssetSelector.Select([empty, pending]);

        Assert.Null(selected);
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        GitHubReleaseAsset? selected = PortableAssetSelector.Select([Asset("ducom.EXE")]);

        Assert.NotNull(selected);
        Assert.Equal("ducom.EXE", selected.Name);
    }
}
