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

    [Fact]
    public void LogAnalyzerPreferencesRoundTripSourceSelections()
    {
        string path = TempPath();
        LogAnalyzerPreferencesService service = new(path);
        LogAnalyzerPreferences expected = new(
            Width: 1280,
            Height: 760,
            RealtimeMode: true,
            NavigationMode: "Time",
            FollowLatest: false,
            SourceCalibrationExpanded: true,
            Sources: new Dictionary<string, LogAnalyzerSourcePreference>
            {
                ["COM42"] = new(false, "左", 12.5),
            },
            Columns: new LogAnalyzerColumnVisibility(Source: true, Time: false, Message: true, Role: false,
                Level: false, Module: true, Keywords: false, Comment: true));

        service.Save(expected);
        LogAnalyzerPreferences actual = service.Load();

        Assert.True(actual.RealtimeMode);
        Assert.Equal("Time", actual.NavigationMode);
        Assert.False(actual.FollowLatest);
        Assert.True(actual.SourceCalibrationExpanded);
        Assert.False(actual.Sources!["COM42"].IsSelected);
        Assert.Equal("左", actual.Sources["COM42"].Role);
        Assert.Equal(12.5, actual.Sources["COM42"].OffsetMilliseconds);
        Assert.False(actual.Columns!.Time);
        Assert.False(actual.Columns.Role);
        Assert.False(actual.Columns.Level);
        Assert.False(actual.Columns.Keywords);
        Assert.True(actual.Columns.Message);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"ducom-analysis-{Guid.NewGuid():N}.json");
}
