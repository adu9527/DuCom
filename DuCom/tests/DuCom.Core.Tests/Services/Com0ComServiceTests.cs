using DuCom.Core.Processes;

namespace DuCom.Core.Tests.Services;

public sealed class Com0ComServiceTests
{
    [Fact]
    public void ResolveSetupcPath_PrefersAutomaticDiscoveryThenSavedPath()
    {
        HashSet<string> existing = ["auto", "saved"];

        Assert.Equal("auto", Com0ComParser.ResolveSetupcPath(["missing", "auto"], "saved", existing.Contains));
        Assert.Equal("saved", Com0ComParser.ResolveSetupcPath(["missing"], "saved", existing.Contains));
        Assert.Equal(string.Empty, Com0ComParser.ResolveSetupcPath(["missing"], "also-missing", existing.Contains));
    }

    [Fact]
    public void ParseList_PreservesExistingPairOptions()
    {
        const string output = """
            CNCA7 PortName=COM21,EmuBR=yes,EmuOverrun=no,HiddenMode=yes,PlugInMode=no,ExclusiveMode=yes,EmuNoise=0.25,AddRTTO=12,AddRITO=34
            CNCB7 PortName=COM22,EmuBR=yes,HiddenMode=yes
            """;

        IReadOnlyList<Com0ComPortEntry> entries = Com0ComParser.ParseList(output);
        Com0ComPortPair pair = Assert.Single(Com0ComParser.PairEntries(entries));

        Assert.Equal("COM21", pair.SideA.PortName);
        Assert.Equal("yes", pair.SideA.Options["EmuBR"]);
        Assert.Equal("no", pair.SideA.Options["EmuOverrun"]);
        Assert.Equal("yes", pair.SideA.Options["HiddenMode"]);
        Assert.Equal("0.25", pair.SideA.Options["EmuNoise"]);
        Assert.Equal("12", pair.SideA.Options["AddRTTO"]);
        Assert.Equal("34", pair.SideA.Options["AddRITO"]);
    }

    [Theory]
    [InlineData("list", true)]
    [InlineData(" change CNCA0 EmuBR=yes", true)]
    [InlineData("INSTALL PortName=COM1", true)]
    [InlineData("remove 0", true)]
    [InlineData("update CNCA0", false)]
    [InlineData("listening", false)]
    public void IsAllowedArguments_RequiresWhitelistedFirstToken(string arguments, bool expected)
    {
        Assert.Equal(expected, Com0ComParser.IsAllowedArguments(arguments, ["list", "install", "remove", "change"]));
    }
}
