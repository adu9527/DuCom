using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class SettingsCatalogTests
{
    [Fact]
    public void OptionsAndDefaultsDescribeApplicationProfile()
    {
        Assert.Equal([5, 6, 7, 8], SettingsCatalog.DataBitsOptions);
        Assert.Contains("utf-8", SettingsCatalog.EncodingOptions);
        Assert.Contains(SettingsCatalog.DefaultTimestampFormat, SettingsCatalog.TimestampFormatOptions);
        Assert.Equal(1_152_000, SettingsCatalog.DefaultBaudRate);
        Assert.Equal(40, SettingsCatalog.DefaultLogRotationMegabytes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(12, 12)]
    [InlineData(100, 60)]
    public void RefreshIntervalNormalizationClampsToSupportedRange(int value, int expected)
    {
        Assert.Equal(expected, SettingsCatalog.NormalizeRefreshInterval(value));
    }

    [Fact]
    public void FontNormalizationUsesAvailableFamilyOrDefault()
    {
        Assert.Equal("Consolas", SettingsCatalog.NormalizeLogFontFamily("Consolas", ["Consolas"]));
        Assert.Equal(SettingsCatalog.DefaultLogFontFamily, SettingsCatalog.NormalizeLogFontFamily("Missing", ["Consolas"]));
        Assert.Equal("HH:mm:ss", SettingsCatalog.NormalizeTimestampFormat("HH:mm:ss", ["HH:mm:ss"]));
        Assert.Equal(SettingsCatalog.DefaultTimestampFormat, SettingsCatalog.NormalizeTimestampFormat("invalid", ["HH:mm:ss"]));
    }
}
