using DuCom.Core.Parsing;

namespace DuCom.Core.Tests.Parsing;

public sealed class HighlightFilterRuleServiceTests : IDisposable
{
    private readonly string _tempDirectory;

    public HighlightFilterRuleServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"DuComHighlightFilterTests-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyList()
    {
        string path = Path.Combine(_tempDirectory, "missing.json");
        var service = new HighlightFilterRuleService(path);

        IReadOnlyList<HighlightFilterRule> rules = service.Load();

        Assert.Empty(rules);
    }

    [Fact]
    public void SaveAndLoad_RoundTripPreservesRules()
    {
        string path = Path.Combine(_tempDirectory, "rules.json");
        var service = new HighlightFilterRuleService(path);
        HighlightFilterRule[] rules =
        [
            new(
                Guid.NewGuid(),
                "Errors",
                HighlightFilterRuleKind.Highlight,
                RuleMatchMode.Regex,
                "error",
                false,
                true,
                0xFF,
                0x00,
                0x00,
                null,
                null,
                null,
                Bold: true,
                Italic: true),
            new(
                Guid.NewGuid(),
                "Warnings",
                HighlightFilterRuleKind.Filter,
                RuleMatchMode.Contains,
                "warn",
                true,
                false,
                null,
                null,
                null,
                null,
                null,
                null),
        ];

        service.Save(rules);
        IReadOnlyList<HighlightFilterRule> loaded = service.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("Errors", loaded[0].Name);
        Assert.Equal(HighlightFilterRuleKind.Highlight, loaded[0].Kind);
        Assert.Equal(RuleMatchMode.Regex, loaded[0].Mode);
        Assert.Equal("error", loaded[0].Pattern);
        Assert.False(loaded[0].IsCaseSensitive);
        Assert.True(loaded[0].IsEnabled);
        Assert.Equal((byte?)0xFF, loaded[0].ForegroundR);
        Assert.Equal((byte?)0x00, loaded[0].ForegroundG);
        Assert.Equal((byte?)0x00, loaded[0].ForegroundB);
        Assert.True(loaded[0].Bold);
        Assert.True(loaded[0].Italic);

        Assert.Equal("Warnings", loaded[1].Name);
        Assert.Equal(HighlightFilterRuleKind.Filter, loaded[1].Kind);
        Assert.True(loaded[1].IsCaseSensitive);
        Assert.False(loaded[1].IsEnabled);
        Assert.False(loaded[1].Bold);
        Assert.False(loaded[1].Italic);
    }

    [Fact]
    public void LegacyFlatRulesLoadIntoDefaultProject()
    {
        string path = Path.Combine(_tempDirectory, "legacy-rules.json");
        File.WriteAllText(path, """
            [
              {
                "id": "7d826fba-22f2-41a1-883f-f0a6870936be",
                "name": "Errors",
                "kind": "highlight",
                "mode": "contains",
                "pattern": "error",
                "isEnabled": true
              }
            ]
            """);
        var service = new HighlightFilterRuleService(path);

        HighlightFilterRuleProject project = Assert.Single(service.LoadProjects());

        Assert.Equal("default", project.Name);
        Assert.Single(project.Rules);
    }

    [Fact]
    public void Load_InvalidJson_ReturnsEmptyList()
    {
        string path = Path.Combine(_tempDirectory, "bad.json");
        File.WriteAllText(path, "{ not valid json");
        var service = new HighlightFilterRuleService(path);

        IReadOnlyList<HighlightFilterRule> rules = service.Load();

        Assert.Empty(rules);
    }

    [Fact]
    public void Save_CreatesDirectoryWhenMissing()
    {
        string path = Path.Combine(_tempDirectory, "nested", "rules.json");
        var service = new HighlightFilterRuleService(path);

        service.Save([CreateSampleRule()]);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_EmptyList_WritesEmptyArray()
    {
        string path = Path.Combine(_tempDirectory, "empty.json");
        var service = new HighlightFilterRuleService(path);

        service.Save([]);

        string json = File.ReadAllText(path);
        Assert.Contains("[]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingId_GeneratesNewGuid()
    {
        string path = Path.Combine(_tempDirectory, "no-id.json");
        File.WriteAllText(path, """
            [
              {
                "name": "NoId",
                "kind": "highlight",
                "mode": "contains",
                "pattern": "x",
                "isCaseSensitive": false,
                "isEnabled": true,
                "foregroundR": 255,
                "foregroundG": 255,
                "foregroundB": 255
              }
            ]
            """);
        var service = new HighlightFilterRuleService(path);

        IReadOnlyList<HighlightFilterRule> rules = service.Load();

        HighlightFilterRule rule = Assert.Single(rules);
        Assert.NotEqual(Guid.Empty, rule.Id);
        Assert.Equal("NoId", rule.Name);
    }

    private static HighlightFilterRule CreateSampleRule()
    {
        return new HighlightFilterRule(
            Guid.NewGuid(),
            "Sample",
            HighlightFilterRuleKind.Highlight,
            RuleMatchMode.Contains,
            "sample",
            false,
            true,
            0x00,
            0xFF,
            0x00,
            null,
            null,
            null);
    }
}
