using DuCom.Core.Updates;

namespace DuCom.Core.Tests.Updates;

public sealed class UpdateVersionTests
{
    [Theory]
    [InlineData("V0.0.0.3", 0, 0, 0, 3)]
    [InlineData("v0.0.0.10", 0, 0, 0, 10)]
    [InlineData("V1.2.3.4", 1, 2, 3, 4)]
    [InlineData("V0.0.3", 0, 0, 3, 0)]
    [InlineData("1.0", 1, 0, 0, 0)]
    [InlineData("V2", 2, 0, 0, 0)]
    [InlineData(" V0.1.0.0 ", 0, 1, 0, 0)]
    [InlineData("V1.0.0-beta.1", 1, 0, 0, 0)]
    [InlineData("V0003", 0, 0, 0, 3)]
    [InlineData("V0100", 0, 0, 0, 100)]
    public void TryParseAcceptsSupportedTagShapes(string tag, int major, int minor, int build, int revision)
    {
        Version? parsed = UpdateVersion.TryParse(tag);

        Assert.Equal(new Version(major, minor, build, revision), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("V")]
    [InlineData("V-rc1")]
    [InlineData("V1.2.3.4.5")]
    [InlineData("V1.x.2")]
    [InlineData("Vabc")]
    [InlineData("V0003a")]
    [InlineData("V1..2")]
    public void TryParseRejectsUnsupportedTags(string? tag) => Assert.Null(UpdateVersion.TryParse(tag));

    [Fact]
    public void LegacyFourDigitTagMatchesExpandedEquivalent()
    {
        Version? legacy = UpdateVersion.TryParse("V0003");
        Version? expanded = UpdateVersion.TryParse("V0.0.0.3");

        Assert.Equal(expanded, legacy);
    }

    [Fact]
    public void IsNewerComparesFullFourPartVersion()
    {
        Assert.True(UpdateVersion.IsNewer(new Version(0, 0, 0, 4), new Version(0, 0, 0, 3)));
        Assert.True(UpdateVersion.IsNewer(new Version(0, 0, 1, 0), new Version(0, 0, 0, 99)));
        Assert.True(UpdateVersion.IsNewer(new Version(1, 0, 0, 0), new Version(0, 9, 9, 9)));
        Assert.False(UpdateVersion.IsNewer(new Version(0, 0, 0, 3), new Version(0, 0, 0, 3)));
        Assert.False(UpdateVersion.IsNewer(new Version(0, 0, 0, 2), new Version(0, 0, 0, 3)));
    }

    [Fact]
    public void ToDisplayStringPrefixesVersionWithV()
    {
        Assert.Equal("V0.0.0.3", UpdateVersion.ToDisplayString(new Version(0, 0, 0, 3)));
        Assert.Equal("V1.0.0.0", UpdateVersion.ToDisplayString(new Version(1, 0, 0, 0)));
    }
}
