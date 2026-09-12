using System.IO;
using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Services;

internal static class ConfigurationSnapshotIo
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string Serialize(ConfigurationSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, JsonOptions);

    internal static bool TryDeserialize(
        string json,
        out ConfigurationSnapshot? snapshot,
        out IReadOnlyList<string> skippedProperties) =>
        TolerantJsonLoader.TryLoad(json, JsonOptions, out snapshot, out skippedProperties);

    internal static void Export(string path, ConfigurationSnapshot snapshot) =>
        File.WriteAllText(path, Serialize(snapshot));

    internal static bool TryImport(
        string path,
        out ConfigurationSnapshot? snapshot,
        out IReadOnlyList<string> skippedProperties) =>
        TryDeserialize(File.ReadAllText(path), out snapshot, out skippedProperties);
}
