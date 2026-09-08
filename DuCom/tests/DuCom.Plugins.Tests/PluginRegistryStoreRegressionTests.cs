using DuCom.PluginHost.Registry;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class PluginRegistryStoreRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ducom-registry-regression-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":999}")]
    [InlineData("{\"plugins\":null}")]
    [InlineData("{\"pendingNotices\":null}")]
    [InlineData("{\"plugins\":{\"x\":null}}")]
    public void InvalidRegistryFailsClosedAndRemainsSafeAfterMutationAndReload(string json)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "registry.json");
        File.WriteAllText(path, json);
        PluginRegistryStore store = new(path);
        store.Load();
        Assert.True(store.Current.SafeStartAllPlugins);
        Assert.Empty(store.Current.Plugins);
        Assert.Equal(json, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "*.corrupt-*"))));
        store.Mutate(data => { data.Plugins["org.example.test"] = new PluginRegistryEntry { Id = "org.example.test" }; });
        PluginRegistryStore reloaded = new(path);
        reloaded.Load();
        Assert.True(reloaded.Current.SafeStartAllPlugins);
        Assert.Single(reloaded.Current.Plugins);
        Assert.NotEqual(store.HostRunId, reloaded.HostRunId);
    }

    [Fact]
    public void MissingRegistryIsNormalFirstRun()
    {
        PluginRegistryStore store = new(Path.Combine(_root, "registry.json"));
        store.Load();
        Assert.False(store.Current.SafeStartAllPlugins);
        Assert.Empty(store.Current.Plugins);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
