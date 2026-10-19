using System.Reflection;
using System.Reflection.Emit;
using DuCom.Services.Updates;
using Xunit;

namespace DuCom.App.Tests;

public sealed class BuildDistributionTests
{
    [Theory]
    [InlineData("Installed", DistributionKind.Installed)]
    [InlineData("PortableFull", DistributionKind.PortableFull)]
    [InlineData("PortableLite", DistributionKind.PortableLite)]
    public void ReadsEmbeddedDistribution(string value, DistributionKind expected)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!,
            ["Distribution", value]));
        Assert.Equal(expected, BuildDistribution.Read(assembly));
    }

    [Fact]
    public void ApplicationHasExplicitBuildIdentity() =>
        Assert.True(Enum.IsDefined(BuildDistribution.Current));
}
