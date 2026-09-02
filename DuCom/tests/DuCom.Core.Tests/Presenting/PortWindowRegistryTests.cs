using DuCom.Core.Presenting;

namespace DuCom.Core.Tests.Presenting;

public sealed class PortWindowRegistryTests
{
    private sealed class FakeWindow
    {
        public int ActivateCount;
        public int CloseCount;
    }

    private static PortWindowRegistry<FakeWindow> CreateRegistry() => new(
        window => window.ActivateCount++,
        window => window.CloseCount++);

    [Fact]
    public void GetOrOpen_CreatesOnceThenActivates()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();
        int factoryCalls = 0;

        FakeWindow first = registry.GetOrOpen("COM3", _ =>
        {
            factoryCalls++;
            return new FakeWindow();
        });
        FakeWindow second = registry.GetOrOpen("COM3", _ => throw new InvalidOperationException("must not be called"));

        Assert.Same(first, second);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, first.ActivateCount);
        Assert.Equal(0, first.CloseCount);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void PortNames_AreCaseInsensitive()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();

        FakeWindow upper = registry.GetOrOpen("COM3", _ => new FakeWindow());
        FakeWindow lower = registry.GetOrOpen("com3", _ => new FakeWindow());

        Assert.Same(upper, lower);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.IsOpen("Com3"));
    }

    [Fact]
    public void DifferentPorts_AreIsolated()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();

        FakeWindow a = registry.GetOrOpen("COM3", _ => new FakeWindow());
        FakeWindow b = registry.GetOrOpen("COM5", _ => new FakeWindow());

        Assert.NotSame(a, b);
        Assert.Equal(2, registry.Count);
        Assert.True(registry.IsOpen("COM3"));
        Assert.True(registry.IsOpen("COM5"));
        Assert.False(registry.IsOpen("COM7"));
        Assert.Equal([a, b], registry.Windows);
    }

    [Fact]
    public void Windows_ReturnsSnapshot()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();
        FakeWindow window = registry.GetOrOpen("COM3", _ => new FakeWindow());

        IReadOnlyList<FakeWindow> snapshot = registry.Windows;
        registry.Close("COM3");

        Assert.Equal([window], snapshot);
        Assert.Empty(registry.Windows);
    }

    [Fact]
    public void Close_ClosesWindowAndRemovesRegistration()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();
        FakeWindow window = registry.GetOrOpen("COM3", _ => new FakeWindow());

        bool closed = registry.Close("COM3");

        Assert.True(closed);
        Assert.Equal(1, window.CloseCount);
        Assert.Equal(0, registry.Count);
        Assert.False(registry.IsOpen("COM3"));
    }

    [Fact]
    public void Close_UnknownPort_ReturnsFalse()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();

        Assert.False(registry.Close("COM9"));
    }

    [Fact]
    public void CloseAll_ClosesEveryWindowAndClears()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();
        FakeWindow first = registry.GetOrOpen("COM3", _ => new FakeWindow());
        FakeWindow second = registry.GetOrOpen("COM5", _ => new FakeWindow());

        registry.CloseAll();

        Assert.Equal(1, first.CloseCount);
        Assert.Equal(1, second.CloseCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void CloseAll_ContinuesWhenCloseCallbackThrows()
    {
        int closeAttempts = 0;
        PortWindowRegistry<FakeWindow> registry = new(
            _ => { },
            _ =>
            {
                closeAttempts++;
                throw new InvalidOperationException("close failed");
            });
        _ = registry.GetOrOpen("COM3", _ => new FakeWindow());
        _ = registry.GetOrOpen("COM5", _ => new FakeWindow());

        registry.CloseAll();

        Assert.Equal(2, closeAttempts);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Remove_UnregistersWithoutClosing()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();
        FakeWindow window = registry.GetOrOpen("COM3", _ => new FakeWindow());

        bool removed = registry.Remove("COM3");

        Assert.True(removed);
        Assert.Equal(0, window.CloseCount);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void FactoryFailure_DoesNotRegister()
    {
        PortWindowRegistry<FakeWindow> registry = CreateRegistry();

        Assert.Throws<InvalidOperationException>(() => registry.GetOrOpen("COM3", _ => throw new InvalidOperationException("boom")));

        Assert.Equal(0, registry.Count);
        Assert.False(registry.IsOpen("COM3"));
    }
}
