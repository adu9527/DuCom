using DuCom.Core.Diagnostics;

namespace DuCom.Core.Tests.Diagnostics;

public sealed class StandardLoadScenariosTests
{
    [Fact]
    public void CatalogContainsAllInitialM0Scenarios()
    {
        string[] names = StandardLoadScenarios.All.Select(scenario => scenario.Name).ToArray();

        Assert.Equal(
            [
                "dual-1m-mixed",
                "dual-1152000-sustained",
                "dual-3m-burst",
                "no-newline-continuous",
                "malformed-text-esc",
                "slow-log-target",
                "failing-log-target",
            ],
            names);
    }

    [Fact]
    public void BaudEquivalentScenariosUseTenBitsPerSerialByte()
    {
        StandardLoadScenario sustained = StandardLoadScenarios.Get("dual-1152000-sustained");
        StandardLoadScenario stress = StandardLoadScenarios.Get("dual-3m-burst");

        Assert.Equal(115_200, sustained.GeneratorOptions.TargetBytesPerSecondPerPort);
        Assert.Equal(300_000, stress.GeneratorOptions.TargetBytesPerSecondPerPort);
    }

    [Fact]
    public void UnknownScenarioIsRejectedWithAvailableNames()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => StandardLoadScenarios.Get("missing"));

        Assert.Contains("dual-1m-mixed", exception.Message);
    }
}
