using DuCom.Plugins.Production;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class CompoundCommandTests
{
    [Fact]
    public async Task ReadbackMustMatchOppositeMacAndRejectAllZeros()
    {
        ProductionCommand group = new() { Steps = [new() { Id = "local", Name = "本机" }, new() { Id = "peer", Name = "对耳" }], Comparisons = [new() { ActualStepId = "peer", ExpectedStepId = "local" }] };
        string[] macs = ["11:22:33:44:55:66", "AA:BB:CC:DD:EE:FF"];
        var ok = await CompoundCommandRunner.RunAsync(group, [0, 1], (side, step, _) => Task.FromResult(macs[step.Id == "local" ? side : 1 - side]), default);
        Assert.All(ok, r => Assert.True(r.Success));
        var mismatch = await CompoundCommandRunner.RunAsync(group, [0, 1], (side, _, _) => Task.FromResult(macs[side]), default);
        Assert.All(mismatch, r => Assert.False(r.Success));
        var zeros = await CompoundCommandRunner.RunAsync(group, [0, 1], (_, _, _) => Task.FromResult("00:00:00:00:00:00"), default);
        Assert.All(zeros, r => Assert.False(r.Success));
    }
    [Fact]
    public async Task OrdinaryCompoundUsesSelectedChannelsAndChosenParsedOutput()
    {
        ProductionCommand group = new() { Name = "读取状态", OutputStepId = "read", Steps = [new() { Id = "open", Name = "准备", DelayMs = 0 }, new() { Id = "read", Name = "电量", DelayMs = 0 }] };
        List<string> calls = [];
        var results = await CompoundCommandRunner.RunAsync(group, [0, 1], (side, step, _) => { calls.Add($"{side}:{step.Id}"); return Task.FromResult(step.Id == "read" ? side == 0 ? "99%" : "98%" : "已应答"); }, default);
        Assert.Equal(new[] { "0:open", "1:open", "0:read", "1:read" }, calls);
        Assert.Equal("99%", results[0].Text); Assert.Equal("98%", results[1].Text);
        Assert.All(results, r => Assert.True(r.Success));
    }

    [Fact]
    public async Task FailedSideStopsItsStepsAndOtherSideCompletes()
    {
        ProductionCommand group = new() { SuccessText = "完成", Steps = [new() { Id = "first", Name = "第一条" }, new() { Id = "second", Name = "第二条" }] };
        List<string> calls = [];
        var results = await CompoundCommandRunner.RunAsync(group, [0, 1], (side, step, _) => { calls.Add($"{side}:{step.Id}"); if (side == 0) throw new TimeoutException(); return Task.FromResult("已应答"); }, default);
        Assert.False(results[0].Success); Assert.Contains("第一条", results[0].Text); Assert.True(results[1].Success);
        Assert.DoesNotContain("0:second", calls); Assert.Contains("1:second", calls);
    }

    [Fact]
    public async Task CrossChannelFailureNeverRunsLaterWriteAndOutputValidationRejectsMissingStep()
    {
        ProductionCommand group = ProductionCommand.CreateX8IPairing();
        int calls = 0;
        var results = await CompoundCommandRunner.RunAsync(group, [0, 1], (_, _, _) => { calls++; throw new TimeoutException(); }, default);
        Assert.Equal(1, calls); Assert.All(results, r => Assert.False(r.Success));
        group.OutputStepId = "missing";
        Assert.Throws<InvalidDataException>(() => new ProductionProfile { Commands = [group] }.Validate());
    }
}
