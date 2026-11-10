using DuCom.PluginHost;
using DuCom.PluginHost.Core;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class BudgetGovernorSamplingTests
{
    private const string Bes = "internal.ducom.bes-download";
    private static BudgetGovernor Create(Dictionary<int, long> memory, BudgetGovernorConfig? config = null) =>
        new(config ?? new BudgetGovernorConfig { EmergencyStopCooldownMilliseconds = 0 },
            pid => new BudgetGovernor.ProcessMemorySnapshot(memory.GetValueOrDefault(pid, 100_000_000), 1));

    [Fact]
    public void OwnQuotaSelectionComparesExcessAgainstEachPluginsQuota()
    {
        BudgetStopDecision? decision = BudgetGovernor.DecideEmergencyStop(
            100_000_000,
            [(Bes, 10, 210_000_000), ("standard", 11, 255_000_000)],
            new Dictionary<uint, long>(), 0,
            new BudgetLimits(800_000_000, 250_000_000, 200_000_000, 250_000_000,
                450_000_000, 32 * 1024 * 1024, Bes), requireEvidence: true);
        Assert.NotNull(decision);
        Assert.Equal(Bes, decision.Value.PluginId);
        Assert.Equal(BudgetStopReason.QuotaExceeded, decision.Value.Reason);
    }

    [Fact]
    public void RealSampleEnforcesOwnQuotaBelowTotal()
    {
        Dictionary<int, long> memory = new() { [10] = 210_000_000 };
        using BudgetGovernor budget = Create(memory);
        budget.RegisterWorker(Bes, 10, "a1");
        BudgetStopRequest? stop = null;
        budget.EmergencyStop += request => stop = request;
        PluginBudgetSample sample = budget.Sample();
        Assert.True(sample.TotalPrivateBytes < budget.Configuration.TotalBudgetBytes);
        Assert.Equal(BudgetStopReason.QuotaExceeded, stop!.Reason);
        Assert.Equal("a1", stop.ActivationId);
        Assert.Equal(10u, stop.Pid);
    }

    [Fact]
    public void RealSampleEnforcesSharedGroupBelowTotal()
    {
        Dictionary<int, long> memory = new() { [10] = 140_000_000, [11] = 120_000_000 };
        using BudgetGovernor budget = Create(memory);
        budget.RegisterWorker("one", 10, "a1");
        budget.RegisterWorker("two", 11, "a2");
        BudgetStopRequest? stop = null;
        budget.EmergencyStop += request => stop = request;
        budget.Sample();
        Assert.Equal(BudgetStopReason.GroupQuotaExceeded, stop!.Reason);
        Assert.Equal(10u, stop.Pid);
    }

    [Fact]
    public void FastGrowthBelowAllBudgetsNeverStopsHealthyPlugin()
    {
        Dictionary<int, long> memory = new() { [10] = 40_000_000 };
        using BudgetGovernor budget = Create(memory);
        budget.RegisterWorker("one", 10, "a1");
        List<BudgetStopRequest> stops = [];
        budget.EmergencyStop += stops.Add;
        budget.Sample();
        memory[10] = 120_000_000;
        budget.Sample();
        Assert.Empty(stops);
    }

    [Fact]
    public void WorkerAndAllHelpersSharePluginQuotaWithoutChangingWorkersSnapshot()
    {
        Dictionary<int, long> memory = new() { [10] = 60_000_000, [20] = 100_000_000, [21] = 100_000_000 };
        using BudgetGovernor budget = Create(memory);
        budget.RegisterWorker("one", 10, "a1");
        budget.RegisterMemoryMonitorHelper("one", "a1", 20);
        budget.RegisterMemoryMonitorHelper("one", "a1", 21);
        BudgetStopRequest? stop = null;
        budget.EmergencyStop += request => stop = request;
        PluginBudgetSample sample = budget.Sample();
        Assert.Equal(60_000_000, Assert.Single(sample.Workers).PrivateBytes);
        Assert.Equal(2, sample.Helpers.Count);
        Assert.Equal(260_000_000, sample.StandardPluginBytes);
        Assert.Equal(360_000_000, sample.TotalPrivateBytes);
        Assert.Equal(260_000_000, stop!.PrivateBytes);
        Assert.Equal(10u, stop.Pid);
        Assert.Equal("a1", stop.ActivationId);
    }

    [Fact]
    public void BesHelperUsesBesBudgetAndRemainsChargedUntilConfirmedExit()
    {
        Dictionary<int, long> memory = new() { [10] = 60_000_000, [20] = 150_000_000 };
        using BudgetGovernor budget = Create(memory);
        budget.RegisterWorker(Bes, 10, "a1");
        budget.RegisterMemoryMonitorHelper(Bes, "a1", 20);
        budget.Sample();
        budget.UnregisterWorker(10);
        memory[20] = -1; // Unreadable is not confirmed exit.
        PluginBudgetSample retained = budget.Sample();
        Assert.Empty(retained.Workers);
        Assert.Equal(150_000_000, retained.BesPluginBytes);
        Assert.Equal(0, retained.StandardPluginBytes);
        Assert.False(budget.CanActivatePlugins(Bes));
        budget.UnregisterMemoryMonitorHelper(20);
        Assert.Equal(0, budget.Sample().BesPluginBytes);
        Assert.True(budget.CanActivatePlugins(Bes));
    }

    [Fact]
    public void SampledCallbackCannotTurnOldRequestIntoNewGenerationStop()
    {
        Dictionary<int, long> memory = new() { [10] = 260_000_000 };
        using BudgetGovernor budget = Create(memory);
        budget.RegisterWorker("one", 10, "old");
        budget.Sampled += _ => budget.RegisterWorker("one", 10, "new");
        List<BudgetStopRequest> stops = [];
        budget.EmergencyStop += stops.Add;
        budget.Sample();
        Assert.Empty(stops);
    }

    [Fact]
    public void OldHelperDebtDoesNotKillHealthyReplacement()
    {
        Dictionary<int, long> memory = new() { [10] = 60_000_000, [20] = 210_000_000 };
        using BudgetGovernor budget = Create(memory);
        budget.RegisterWorker("one", 10, "new");
        budget.RegisterMemoryMonitorHelper("one", "old", 20);
        List<BudgetStopRequest> stops = [];
        budget.EmergencyStop += stops.Add;
        Assert.Equal(270_000_000, budget.Sample().StandardPluginBytes);
        Assert.Empty(stops);
    }

    [Fact]
    public void StopConsumerMatchesPluginPidAndActivation()
    {
        using BudgetGovernor budget = Create(new());
        BudgetStopRequest request = new() { PluginId = "one", Pid = 10, ActivationId = "old", PrivateBytes = 1, Sample = budget.Sample() };
        Assert.True(PluginSystemService.MatchesBudgetStop(request, "one", 10, "old"));
        Assert.False(PluginSystemService.MatchesBudgetStop(request, "one", 10, "new"));
        Assert.False(PluginSystemService.MatchesBudgetStop(request, "other", 10, "old"));
        Assert.False(PluginSystemService.MatchesBudgetStop(request, "one", 11, "old"));
    }

    [Fact]
    public async Task RecoverySkipsBlockedGroupAndStopsOnlyAfterSuccessfulRecovery()
    {
        Dictionary<int, long> memory = new() { [10] = 180_000_000 };
        using BudgetGovernor budget = Create(memory);
        budget.RegisterWorker("running-standard", 10, "active");
        budget.Sample();
        List<string> attempts = [];
        bool recovered = await PluginSystemService.TryRecoverBudgetQueueAsync(["a-standard", Bes, "z-unused"], pluginId =>
        {
            attempts.Add(pluginId);
            return Task.FromResult(budget.CanActivatePlugins(pluginId));
        });
        Assert.True(recovered);
        Assert.Equal(new[] { "a-standard", Bes }, attempts);
    }

    [Theory]
    [InlineData(Bes, 200_000_000)]
    [InlineData("standard", 250_000_000)]
    public void HelperHardLimitDoesNotExceedOwningPluginQuota(string pluginId, long expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "helper-quota-" + Guid.NewGuid().ToString("N"));
        try
        {
            using BudgetGovernor budget = Create(new());
            using NativeHelperTaskManager manager = new(new DuCom.Plugin.PluginManifest { Id = pluginId }, root, root, budget, "a1");
            Assert.Equal(expected, manager.HelperMemoryLimitBytes);
            Assert.Equal(budget.GetPluginQuota(pluginId), manager.HelperMemoryLimitBytes);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
