using System.IO.Ports;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class ConfigurationSnapshotNormalizerTests
{
    [Fact]
    public void NormalizeAppliesClampFallbackAndMigrationRules()
    {
        ConfigurationSnapshot snapshot = CreateSnapshot() with
        {
            TimestampFormat = "invalid",
            PrivateMemoryThresholdMiB = 0,
            MemoryRefreshInterval = 0,
            SystemMemoryRefreshInterval = 100,
            LogFontSize = 12,
            LogFontFamily = "Missing Font",
            SearchOpacity = 1.5,
            TelnetPort = 70_000,
            SplitterRatio = 0.1,
        };

        ConfigurationApplyPlan plan = ConfigurationSnapshotNormalizer.Normalize(
            snapshot,
            ["HH:mm:ss", "HH:mm:ss.fff"],
            ["Cascadia Mono", "Consolas"]);

        Assert.Equal("HH:mm:ss.fff", plan.TimestampFormat);
        Assert.Equal(1, plan.PrivateMemoryThresholdMiB);
        Assert.Equal(1, plan.MemoryRefreshInterval);
        Assert.Equal(60, plan.SystemMemoryRefreshInterval);
        Assert.Equal(14, plan.LogFontSize);
        Assert.Equal("Cascadia Mono", plan.LogFontFamily);
        Assert.Equal(1, plan.SearchOpacity);
        Assert.Equal(65_535, plan.TelnetPort);
        Assert.Equal(0.2, plan.SplitterRatio);
    }

    [Fact]
    public void NormalizeCleansPortNamesHiddenPortsAndCustomBaudRates()
    {
        ConfigurationSnapshot snapshot = CreateSnapshot() with
        {
            CustomBaudRates = [115_200, 0, 9_600, 115_200, -1],
            HiddenPorts = [" COM3 ", "com3", "", "COM1"],
            CommandTargetPortNames = [" COM2 ", "com2", "COM10", "", "com1"],
        };

        ConfigurationApplyPlan plan = ConfigurationSnapshotNormalizer.Normalize(
            snapshot,
            ["HH:mm:ss.fff"],
            ["Cascadia Mono"]);

        Assert.Equal([9_600, 115_200], plan.CustomBaudRates);
        Assert.Equal(["COM3", "COM1"], plan.HiddenPorts);
        Assert.Equal(["com1", "COM10", "COM2"], plan.CommandTargetPortNames);
    }

    [Fact]
    public void NormalizeKeepsEmptyCustomBaudRateListAsNoReplacementPlan()
    {
        ConfigurationSnapshot snapshot = CreateSnapshot() with { CustomBaudRates = [] };

        ConfigurationApplyPlan plan = ConfigurationSnapshotNormalizer.Normalize(
            snapshot,
            ["HH:mm:ss.fff"],
            ["Cascadia Mono"]);

        Assert.Null(plan.CustomBaudRates);
    }

    private static ConfigurationSnapshot CreateSnapshot() => new(
        115_200,
        8,
        StopBits.One,
        Parity.None,
        Handshake.None,
        "utf-8",
        ReceiveDisplayMode.Str,
        true,
        true,
        "logs",
        SendMode.Str,
        NewlinePolicy.CrLf);
}
