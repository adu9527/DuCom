using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class SettingsSaveGateTests
{
    [Fact]
    public void DirtyStateIsConsumedExactlyOnce()
    {
        SettingsSaveGate gate = new();

        Assert.False(gate.TryConsumeDirty());
        gate.MarkDirty();
        gate.MarkDirty();

        Assert.True(gate.IsDirty);
        Assert.True(gate.TryConsumeDirty());
        Assert.False(gate.IsDirty);
        Assert.False(gate.TryConsumeDirty());
    }
}
