namespace DuCom.PluginHost.Core;

public sealed class PluginHostPaths
{
    public PluginHostPaths(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = root;
        InstalledRoot = System.IO.Path.Combine(root, "Installed");
        StorageRoot = System.IO.Path.Combine(root, "Data");
        TempRoot = System.IO.Path.Combine(root, "Temp");
        DiagnosticsRoot = System.IO.Path.Combine(root, "Diag");
        RegistryPath = System.IO.Path.Combine(root, "plugin-registry.json");
        foreach (string directory in new[] { Root, InstalledRoot, StorageRoot, TempRoot, DiagnosticsRoot })
        {
            System.IO.Directory.CreateDirectory(directory);
        }
    }

    public string Root { get; }
    public string InstalledRoot { get; }
    public string StorageRoot { get; }
    public string TempRoot { get; }
    public string DiagnosticsRoot { get; }
    public string RegistryPath { get; }

    public static PluginHostPaths CreateDefault()
    {
        string? isolatedRoot = Environment.GetEnvironmentVariable("DUCOM_PLUGIN_ROOT");
        return new PluginHostPaths(!string.IsNullOrWhiteSpace(isolatedRoot)
            ? isolatedRoot
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuCom", "Plugins"));
    }

    public string GetDiagnosticsFilePath(string pluginId) => System.IO.Path.Combine(DiagnosticsRoot, $"{pluginId}.log");
}
