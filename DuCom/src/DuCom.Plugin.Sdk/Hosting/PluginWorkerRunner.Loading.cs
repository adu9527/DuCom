using System.Reflection;
using System.Runtime.Loader;

namespace DuCom.Plugin;

public sealed partial class PluginWorkerRunner
{
    private DuComPlugin LoadPlugin()
    {
        string entryAssemblyPath = Path.Combine(_startup.PluginDirectory, _startup.EntryAssembly);
        if (!File.Exists(entryAssemblyPath))
        {
            throw new FileNotFoundException($"Entry assembly '{_startup.EntryAssembly}' not found in the package.");
        }

        PluginLoadContext loadContext = new(entryAssemblyPath);
        Assembly assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
        Type? type = assembly.GetType(_startup.EntryType);
        if (type is null)
        {
            throw new TypeLoadException($"Entry type '{_startup.EntryType}' not found in {assembly.GetName().Name}.");
        }

        if (!typeof(DuComPlugin).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"Entry type '{type.FullName}' does not derive from {nameof(DuComPlugin)}.");
        }

        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException($"Entry type '{type.FullName}' must declare a public parameterless constructor.");
        }

        object instance = Activator.CreateInstance(type)!;
        return (DuComPlugin)instance;
    }

    private sealed class PluginLoadContext(string entryAssemblyPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);
        private const string SdkAssemblyName = "DuCom.Plugin.Sdk";

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, SdkAssemblyName, StringComparison.Ordinal))
            {
                return null;
            }

            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is not null && File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
