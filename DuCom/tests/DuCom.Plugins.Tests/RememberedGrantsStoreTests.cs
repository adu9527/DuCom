using DuCom.Services.Plugins;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class RememberedGrantsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ducom-remembered-{Guid.NewGuid():N}");

    [Fact]
    public void FileGrantRestoresOnlyTheExactFilePath()
    {
        Directory.CreateDirectory(_root);
        string grantFile = Path.Combine(_root, "grants.json");
        string selectedFile = Path.Combine(_root, "firmware.bin");
        File.WriteAllText(selectedFile, "firmware");
        RememberedGrantsStore store = new(grantFile);
        store.Add("org.example.plugin", selectedFile);

        Assert.Equal(Path.GetFullPath(selectedFile), store.Resolve("org.example.plugin", selectedFile));
        Assert.Null(store.Resolve("org.example.plugin", Path.Combine(selectedFile, "child.bin")));

        RememberedGrantsStore reloaded = new(grantFile);
        Assert.Equal(Path.GetFullPath(selectedFile), reloaded.Resolve("org.example.plugin", selectedFile));
        reloaded.Remove("org.example.plugin", selectedFile);
        Assert.Null(reloaded.Resolve("org.example.plugin", selectedFile));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
