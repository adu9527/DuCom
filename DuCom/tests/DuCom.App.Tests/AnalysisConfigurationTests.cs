using System.IO;
using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class AnalysisConfigurationTests
{
    [Fact]
    public void MonitorStoreMigratesPascalCaseRootArrayAndDefaultsPlot()
    {
        string path = TempPath();
        File.WriteAllText(path, """[{"Id":"00000000-0000-0000-0000-000000000001","Name":"X","PortName":null,"Pattern":"x=(\\d+)","IsEnabled":true,"Order":0}]""");
        VariableMonitorConfiguration loaded = VariableMonitorRuleStore.LoadConfiguration(path);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Single(loaded.Rules);
        Assert.Equal(100, loaded.Plot.IngestionIntervalMs);
    }

    [Fact]
    public void MonitorStoreSkipsBadRulesAndClampsSettings()
    {
        string path = TempPath();
        File.WriteAllText(path, """{"schemaVersion":2,"plot":{"ingestionIntervalMs":1,"visibleWindowSeconds":999999},"rules":[null,{"id":"00000000-0000-0000-0000-000000000002","name":"Y","pattern":"y=(\\d+)","isEnabled":true,"order":0,"scale":"bad"},{"id":"00000000-0000-0000-0000-000000000003","name":"Z","pattern":"z=(\\d+)","isEnabled":true,"order":1}]}""");
        VariableMonitorConfiguration loaded = VariableMonitorRuleStore.LoadConfiguration(path);
        Assert.Single(loaded.Rules);
        Assert.Equal(50, loaded.Plot.IngestionIntervalMs);
        Assert.Equal(86_400, loaded.Plot.VisibleWindowSeconds);
        Assert.NotEmpty(loaded.Diagnostics);
    }

    [Fact]
    public void PreferencesClampGeometryAndTimeWindow()
    {
        string path = TempPath();
        AnalysisWindowPreferencesService service = new(path);
        service.Save(new AnalysisWindowPreferences(new(Left: -999999, Top: -999999, Width: 10, Height: 10, VisibleWindowSeconds: 0), new()));
        AnalysisWindowPreference loaded = service.Load().ProtocolDecoder;
        Assert.True(loaded.Width >= 480);
        Assert.True(loaded.Height >= 320);
        Assert.Equal(1, loaded.VisibleWindowSeconds);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"ducom-analysis-{Guid.NewGuid():N}.json");
}
