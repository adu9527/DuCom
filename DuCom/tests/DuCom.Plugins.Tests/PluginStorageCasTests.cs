using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Core;
using DuCom.PluginHost.Diagnostics;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class PluginStorageCasTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ducom-storage-cas-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WrongRevisionReturnsCurrentDocumentWithoutOverwrite()
    {
        using BrokerFixture fixture = Create();
        StorageCompareExchangeResult first = await Cas(fixture.Broker, 0, "one");
        StorageCompareExchangeResult stale = await Cas(fixture.Broker, 0, "two");

        Assert.True(first.Exchanged);
        Assert.False(stale.Exchanged);
        Assert.Equal(1, stale.Revision);
        Assert.Equal("one", stale.Data);
    }

    [Fact]
    public async Task ConcurrentScopesDoNotLoseUpdates()
    {
        using BrokerFixture first = Create("shared");
        using BrokerFixture second = Create("shared");
        const int workers = 24;

        await Task.WhenAll(Enumerable.Range(0, workers).Select(index => Increment(index % 2 == 0 ? first.Broker : second.Broker)));
        StorageReadResult result = await Read(first.Broker);
        using JsonDocument document = JsonDocument.Parse(result.Data!);
        Assert.Equal(workers, document.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(workers, result.Revision);
    }

    private static async Task Increment(PluginBroker broker)
    {
        while (true)
        {
            StorageReadResult current = await Read(broker);
            int count = current.Data is null ? 0 : JsonDocument.Parse(current.Data).RootElement.GetProperty("count").GetInt32();
            StorageCompareExchangeResult result = await Cas(broker, current.Revision, JsonSerializer.Serialize(new { count = count + 1 }));
            if (result.Exchanged) return;
        }
    }

    private BrokerFixture Create(string id = "plugin")
    {
        string storage = Path.Combine(_root, id);
        Directory.CreateDirectory(storage);
        PluginManifest manifest = new() { Id = id, Name = id, Version = "1.0.0", EntryAssembly = "a.dll", EntryType = "A", Permissions = [Permission.StorageOwn] };
        string temp = Path.Combine(_root, "temp-" + Guid.NewGuid().ToString("N"));
        ActivationScope scope = new(manifest, "activation-" + Guid.NewGuid().ToString("N"), storage, temp,
            Path.Combine(temp, "output"), Path.Combine(temp, "snapshots"), new PluginLimits(), [Permission.StorageOwn]);
        return new BrokerFixture(scope, new PluginBroker(scope, new FakeEnvironment(), new PluginDiagnosticsLog(Path.Combine(_root, Guid.NewGuid().ToString("N") + ".log"), id)));
    }

    private static async Task<StorageReadResult> Read(PluginBroker broker) =>
        (await broker.ExecuteAsync(PluginOps.StorageRead, null, default))!.Value.Deserialize<StorageReadResult>(DtoJson.Options)!;

    private static async Task<StorageCompareExchangeResult> Cas(PluginBroker broker, long revision, string data) =>
        (await broker.ExecuteAsync(PluginOps.StorageCompareExchange, JsonSerializer.SerializeToElement(new StorageCompareExchangeRequest { ExpectedRevision = revision, Data = data }, DtoJson.Options), default))!
            .Value.Deserialize<StorageCompareExchangeResult>(DtoJson.Options)!;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed record BrokerFixture(ActivationScope Scope, PluginBroker Broker) : IDisposable
    {
        public void Dispose() { Broker.Dispose(); Scope.Dispose(); }
    }
}
