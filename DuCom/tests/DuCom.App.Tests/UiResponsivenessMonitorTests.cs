using System.Windows.Threading;
using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class UiResponsivenessMonitorTests
{
    [Fact]
    public async Task ConcurrentAndRepeatedCleanupJoinsOneDisposal()
    {
        UiResponsivenessMonitor monitor = new(Dispatcher.CurrentDispatcher);
        monitor.Start();
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () => await monitor.DisposeAsync())));
        await monitor.DisposeAsync();
    }
}
