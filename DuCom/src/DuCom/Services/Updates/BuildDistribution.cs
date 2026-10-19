using System.Reflection;

namespace DuCom.Services.Updates;

public enum DistributionKind
{
    Installed,
    PortableFull,
    PortableLite,
}

/// <summary>Embedded build identity; independent of executable name or installation path.</summary>
public static class BuildDistribution
{
    public static DistributionKind Current { get; } = Read(typeof(BuildDistribution).Assembly);

    internal static DistributionKind Read(Assembly assembly)
    {
        string? value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "Distribution")?.Value;
        return Enum.TryParse(value, out DistributionKind kind) && Enum.IsDefined(kind)
            ? kind
            : throw new InvalidOperationException("Missing or invalid Distribution build metadata.");
    }
}
