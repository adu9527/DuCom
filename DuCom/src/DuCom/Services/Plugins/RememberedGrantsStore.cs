using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuCom.Core.Persistence;
using DuCom.PluginHost;

namespace DuCom.Services.Plugins;

public sealed record RememberedGrantsData
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("grants")] public Dictionary<string, List<string>> Grants { get; init; } = new(StringComparer.Ordinal);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

/// <summary>
/// Host-side store of user-remembered read grants: paths the user explicitly granted to a
/// plugin with "remember" checked. The grant list is the only thing that can turn a stored
/// plugin path back into a broker read token on later activations.
/// </summary>
public sealed class RememberedGrantsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private RememberedGrantsData _data = new();

    public RememberedGrantsStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        Load();
    }

    public static RememberedGrantsStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom", "Plugins", "Data", "remembered-grants.json"));

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    RememberedGrantsData? loaded = JsonSerializer.Deserialize<RememberedGrantsData>(File.ReadAllText(_path), RememberedGrantsData.JsonOptions);
                    if (loaded is not null)
                    {
                        _data = loaded;
                    }
                }
            }
            catch (Exception)
            {
                _data = new RememberedGrantsData();
            }
        }
    }

    public void Add(string pluginId, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentException.ThrowIfNullOrEmpty(path);
        lock (_gate)
        {
            if (!_data.Grants.TryGetValue(pluginId, out List<string>? paths))
            {
                paths = [];
                _data.Grants[pluginId] = paths;
            }

            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
                Save();
            }
        }
    }

    public void Remove(string pluginId, string path)
    {
        lock (_gate)
        {
            if (_data.Grants.TryGetValue(pluginId, out List<string>? paths)
                && paths.RemoveAll(candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                Save();
            }
        }
    }

    public IReadOnlyList<string> Get(string pluginId)
    {
        lock (_gate)
        {
            return _data.Grants.TryGetValue(pluginId, out List<string>? paths) ? [.. paths] : [];
        }
    }

    public string? Resolve(string pluginId, string requestedPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentException.ThrowIfNullOrEmpty(requestedPath);
        string normalized;
        try
        {
            normalized = Path.GetFullPath(requestedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (string granted in Get(pluginId))
        {
            string grantedNormalized = Path.GetFullPath(granted).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(grantedNormalized, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            if (normalized.StartsWith(grantedNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }
        }

        return null;
    }

    private void Save()
    {
        string json = JsonSerializer.Serialize(_data, RememberedGrantsData.JsonOptions);
        AtomicFileStore.WriteAllText(_path, json);
    }
}
