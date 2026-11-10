using System.IO;

namespace DuCom.Services.Plugins;

/// <summary>
/// The X8I reference template embedded into the executable, so the export feature and
/// the bundled project entry never depend on a loose profiles file next to the exe.
/// The resource is declared in DuCom.csproj from the same repo file the tests validate.
/// </summary>
internal static class ProductionBundledTemplate
{
    internal const string Name = "X8I_V1";
    private const string ResourceName = "DuCom.Bundled.X8I_V1.json";
    private const string GenericResourceName = "DuCom.Bundled.GenericTemplate.json";

    internal static string LoadJson()
    {
        using Stream? stream = typeof(ProductionBundledTemplate).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"内嵌模板资源缺失：{ResourceName}");
        using StreamReader reader = new(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The vendor-neutral template handed out via 导出公共模版: generic placeholders and
    /// in-Notes documentation, so customers with different headers/CMD/length layouts can
    /// adapt it without knowing anything about the X8I reference project.
    /// </summary>
    internal static string LoadGenericJson()
    {
        using Stream? stream = typeof(ProductionBundledTemplate).Assembly.GetManifestResourceStream(GenericResourceName)
            ?? throw new InvalidOperationException($"内嵌模板资源缺失：{GenericResourceName}");
        using StreamReader reader = new(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
