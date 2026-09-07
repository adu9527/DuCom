using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Core.Tests.Persistence;

public sealed class TolerantJsonLoaderTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private sealed record TestSnapshot(
        int Port,
        string Name,
        DayOfWeek Day = DayOfWeek.Friday,
        double Ratio = 0.5,
        List<string>? Ports = null);

    [Fact]
    public void ValidJsonLoadsStrictly()
    {
        string json = """{"Port": 9600, "Name": "probe", "Day": 2, "Ratio": 0.75, "Ports": ["COM3"]}""";

        bool loaded = TolerantJsonLoader.TryLoad(json, Options, out TestSnapshot? snapshot, out IReadOnlyList<string> skipped);

        Assert.True(loaded);
        Assert.NotNull(snapshot);
        Assert.Empty(skipped);
        Assert.Equal(9600, snapshot.Port);
        Assert.Equal("probe", snapshot.Name);
        Assert.Equal(DayOfWeek.Tuesday, snapshot.Day);
        Assert.Equal(0.75, snapshot.Ratio);
        Assert.NotNull(snapshot.Ports);
        Assert.Equal(["COM3"], snapshot.Ports);
    }

    [Fact]
    public void OnePoisonedFieldDoesNotDiscardTheSnapshot()
    {
        string json = """{"Port": "not-a-number", "Name": "probe", "Day": 2}""";

        bool loaded = TolerantJsonLoader.TryLoad(json, Options, out TestSnapshot? snapshot, out IReadOnlyList<string> skipped);

        Assert.True(loaded);
        Assert.NotNull(snapshot);
        Assert.Equal(["Port"], skipped);
        Assert.Equal(0, snapshot.Port);
        Assert.Equal("probe", snapshot.Name);
        Assert.Equal(DayOfWeek.Tuesday, snapshot.Day);
        Assert.Equal(0.5, snapshot.Ratio);
    }

    [Fact]
    public void MissingFieldsFallBackToDefaults()
    {
        string json = """{"Name": "probe"}""";

        bool loaded = TolerantJsonLoader.TryLoad(json, Options, out TestSnapshot? snapshot, out IReadOnlyList<string> skipped);

        Assert.True(loaded);
        Assert.NotNull(snapshot);
        Assert.Empty(skipped);
        Assert.Equal(0, snapshot.Port);
        Assert.Equal(DayOfWeek.Friday, snapshot.Day);
        Assert.Equal(0.5, snapshot.Ratio);
        Assert.Null(snapshot.Ports);
    }

    [Fact]
    public void MultiplePoisonedFieldsAreAllReported()
    {
        string json = """{"Port": "oops", "Day": "someday", "Ratio": [1,2,3], "Name": "probe"}""";

        bool loaded = TolerantJsonLoader.TryLoad(json, Options, out TestSnapshot? snapshot, out IReadOnlyList<string> skipped);

        Assert.True(loaded);
        Assert.NotNull(snapshot);
        Assert.Equal(["Port", "Day", "Ratio"], skipped);
        Assert.Equal("probe", snapshot!.Name);
    }

    [Fact]
    public void GarbageJsonIsRejected()
    {
        Assert.False(TolerantJsonLoader.TryLoad("<<not json>>", Options, out TestSnapshot? _, out _));
    }

    [Fact]
    public void NonObjectRootIsRejected()
    {
        Assert.False(TolerantJsonLoader.TryLoad("[1,2,3]", Options, out TestSnapshot? _, out _));
    }

    [Fact]
    public void EmptyJsonIsRejected()
    {
        Assert.False(TolerantJsonLoader.TryLoad("", Options, out TestSnapshot? _, out _));
    }

    [Fact]
    public void PoisonedListPropertyFallsBackToItsDefault()
    {
        string json = """{"Port": 1, "Name": "probe", "Ports": [{"nested": true}]}""";

        bool loaded = TolerantJsonLoader.TryLoad(json, Options, out TestSnapshot? snapshot, out IReadOnlyList<string> skipped);

        Assert.True(loaded);
        Assert.NotNull(snapshot);
        Assert.Equal(["Ports"], skipped);
        Assert.Null(snapshot!.Ports);
    }

    private sealed class PropertyBoundDto
    {
        public int Version { get; set; } = 1;

        public List<string> Items { get; set; } = [];
    }

    [Fact]
    public void ParameterlessTypesBindPerProperty()
    {
        string json = """{"version": 2, "items": ["a", "b"]}""";

        bool loaded = TolerantJsonLoader.TryLoad(json, Options, out PropertyBoundDto? value, out IReadOnlyList<string> skipped);

        Assert.True(loaded);
        Assert.NotNull(value);
        Assert.Empty(skipped);
        Assert.Equal(2, value!.Version);
        Assert.Equal(["a", "b"], value.Items);
    }

    [Fact]
    public void ParameterlessTypesSkipPoisonedProperties()
    {
        string json = """{"version": "two", "items": ["a"]}""";

        bool loaded = TolerantJsonLoader.TryLoad(json, Options, out PropertyBoundDto? value, out IReadOnlyList<string> skipped);

        Assert.True(loaded);
        Assert.NotNull(value);
        Assert.Equal(["Version"], skipped);
        Assert.Equal(1, value!.Version);
        Assert.Equal(["a"], value.Items);
    }

    [Fact]
    public void TryLoadListSkipsUnreadableElements()
    {
        string json = """[{"Port": 1, "Name": "one"}, "garbage", {"Port": "bad", "Name": "poison"}, {"Port": 3, "Name": "three"}]""";

        bool loaded = TolerantJsonLoader.TryLoadList(json, Options, out List<TestSnapshot> values, out IReadOnlyList<int> skipped);

        Assert.True(loaded);
        Assert.Equal([1, 2], skipped);
        Assert.Equal(2, values.Count);
        Assert.Equal("one", values[0].Name);
        Assert.Equal("three", values[1].Name);
    }

    [Fact]
    public void TryLoadListAcceptsCleanArraysStrictly()
    {
        string json = """[{"Port": 1, "Name": "one"}]""";

        bool loaded = TolerantJsonLoader.TryLoadList(json, Options, out List<TestSnapshot> values, out IReadOnlyList<int> skipped);

        Assert.True(loaded);
        Assert.Empty(skipped);
        Assert.Single(values);
    }

    [Fact]
    public void TryLoadListRejectsNonArrayDocuments()
    {
        Assert.False(TolerantJsonLoader.TryLoadList("""{"Port": 1}""", Options, out List<TestSnapshot> _, out _));
        Assert.False(TolerantJsonLoader.TryLoadList("<<not json>>", Options, out List<TestSnapshot> _, out _));
    }

    [Fact]
    public void TryLoadDictionarySkipsUnreadableEntries()
    {
        string json = """{"COM3": {"Width": 600}, "COM4": {"Width": "wide"}, "COM5": 42}""";

        bool loaded = TolerantJsonLoader.TryLoadDictionary(json, Options, out Dictionary<string, MiniLogStyle> values, out IReadOnlyList<string> skipped);

        Assert.True(loaded);
        Assert.Equal(["COM4", "COM5"], skipped);
        Assert.Single(values);
        Assert.Equal(600, values["COM3"].Width);
    }

    private sealed record MiniLogStyle(double Width = 520);
}
