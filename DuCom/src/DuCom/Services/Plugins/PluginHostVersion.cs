using System.Reflection;

namespace DuCom.Services.Plugins;

/// <summary>
/// Separates the four-part Windows/application version from the three-part SemVer contract
/// used by DPP manifests. The revision is intentionally not silently folded into SemVer.
/// </summary>
public static class PluginHostVersion
{
    public static string ApplicationVersion => FormatApplicationVersion(typeof(PluginHostVersion).Assembly.GetName().Version);

    public const string CompatibilityVersion = "0.1.0";

    public static string FormatApplicationVersion(Version? version) => version is null
        ? "0.0.0.0"
        : $"{Math.Max(0, version.Major)}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}.{Math.Max(0, version.Revision)}";
}
