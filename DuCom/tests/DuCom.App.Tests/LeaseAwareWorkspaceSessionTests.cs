using System.Reflection;
using DuCom.Core.Ports;
using DuCom.Core.Sessions;
using DuCom.PluginHost.Core;
using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class LeaseAwareWorkspaceSessionTests
{
    [Fact]
    public async Task PluginLeaseRejectsOpenWithoutThrowingAndAllowsRetryAfterRelease()
    {
        IWorkspaceSession inner = DispatchProxy.Create<IWorkspaceSession, SessionProxy>();
        SessionProxy proxy = (SessionProxy)inner;
        SerialLeaseCoordinator leases = new();
        SerialLeaseSnapshot lease = leases.Acquire("plugin", "activation", "task", "COM40", null, false, false);
        LeaseAwareWorkspaceSession session = new(inner, leases);

        Assert.Equal(PortCommandResult.Faulted, await session.OpenAsync());
        Assert.Equal(0, proxy.OpenCalls);
        Assert.Equal("SerialLease", session.Status.Fault?.Source);
        Assert.Contains("COM40", session.Status.Fault!.Message, StringComparison.Ordinal);

        await leases.RunAsOwnerAsync(lease.LeaseId, async () =>
            Assert.Equal(PortCommandResult.Succeeded, await session.OpenAsync()));
        Assert.Null(session.Status.Fault);
        Assert.Equal(PortCommandResult.Faulted, await session.OpenAsync());

        leases.Release(lease.LeaseId);
        Assert.Equal(PortCommandResult.Succeeded, await session.OpenAsync());
        Assert.Null(session.Status.Fault);
        Assert.Equal(2, proxy.OpenCalls);
    }

    public class SessionProxy : DispatchProxy
    {
        public int OpenCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_Settings": return SerialPortSettings.Default("COM40");
                case "get_Status": return new SerialSessionStatusSnapshot(null!, null!, null);
                case "OpenAsync":
                    OpenCalls++;
                    return Task.FromResult(PortCommandResult.Succeeded);
                default: throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
