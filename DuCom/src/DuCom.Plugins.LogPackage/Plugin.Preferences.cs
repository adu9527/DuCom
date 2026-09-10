using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.LogPackage;

public sealed partial class Plugin
{
    private Task PreFillReproductionTimeAsync()
    {
        lock (_gate)
        {
            // Legacy behavior: every time the packager opens, the reproduction time defaults
            // to that moment (the user can copy the live clock later or edit freely).
            _preferences.ReproductionTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        }

        return Task.CompletedTask;
    }

    private async Task BrowseOutputAsync(CancellationToken cancellationToken)
    {
        FilesPickResult? picked;
        try
        {
            // Directory mode rides on the write permission this plugin already holds; the
            // picked folder is remembered so pack-time target creation can resolve it.
            picked = await Api.Files.PickWriteAsync(FilePickWriteOptions.Directory(), cancellationToken);
        }
        catch (PluginHostException exception) when (string.Equals(exception.Code, PluginErrorCode.Cancelled, StringComparison.Ordinal))
        {
            return;
        }

        if (picked is null)
        {
            return;
        }

        lock (_gate)
        {
            _preferences.OutputDirectory = picked.DisplayPath;
            _preferences.FollowLogDirectory = false;
        }

        await PersistAsync(cancellationToken);
        await PushToolPageAsync();
    }

    private async Task ToggleFollowLogDirectoryAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        bool follow = values.TryGetValue("followLogDirectory", out string? value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        await SaveFormAsync(values.Where(pair => pair.Key != "followLogDirectory").ToDictionary(pair => pair.Key, pair => pair.Value), cancellationToken);
        lock (_gate)
        {
            _preferences.FollowLogDirectory = follow;
            if (follow)
            {
                _preferences.OutputDirectory = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(_preferences.OutputDirectory))
            {
                _preferences.OutputDirectory = _logDirectory;
            }
        }

        await PersistAsync(cancellationToken);
        await PushToolPageAsync();
    }

    private async Task SaveFormAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (values.TryGetValue("projectName", out string? projectName))
            {
                _preferences.ProjectName = projectName ?? string.Empty;
            }

            if (values.TryGetValue("title", out string? title))
            {
                _preferences.Title = title ?? string.Empty;
            }

            if (values.TryGetValue("tester", out string? tester))
            {
                _preferences.Tester = tester ?? string.Empty;
            }

            if (values.TryGetValue("deviceSoftwareVersion", out string? version))
            {
                _preferences.DeviceSoftwareVersion = version ?? string.Empty;
            }

            if (values.TryGetValue("reproductionProbability", out string? probability))
            {
                _preferences.ReproductionProbability = probability ?? string.Empty;
            }

            if (values.TryGetValue("reproductionTime", out string? reproductionTime))
            {
                _preferences.ReproductionTime = reproductionTime ?? string.Empty;
            }

            var selection = values.Where(pair => pair.Key.StartsWith("selection:", StringComparison.Ordinal)).ToArray();
            _preferences.SessionSelection = selection.Length == 0 ? null : selection.ToDictionary(
                pair => pair.Key[10..], pair => string.Equals(pair.Value, "true", StringComparison.OrdinalIgnoreCase), StringComparer.Ordinal);
            Dictionary<string, string> devices = new(_preferences.PortDevices, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values.Where(pair => pair.Key.StartsWith("device:", StringComparison.Ordinal)))
                devices[pair.Key[7..]] = pair.Value;
            _preferences.PortDevices = devices;

            if (values.TryGetValue("problemDescription", out string? description))
            {
                _preferences.ProblemDescription = description ?? string.Empty;
            }

            if (values.TryGetValue("reproductionSteps", out string? steps))
            {
                _preferences.ReproductionSteps = steps ?? string.Empty;
            }

            if (values.TryGetValue("notes", out string? notes))
            {
                _preferences.Notes = notes ?? string.Empty;
            }

            if (values.TryGetValue("portDevices", out string? portDevices))
            {
                _preferences.PortDevices = ParsePortDevices(portDevices);
            }
        }

        await PersistAsync(cancellationToken);
        await PushToolPageAsync();
    }

    private static Dictionary<string, string> ParsePortDevices(string? text)
    {
        Dictionary<string, string> devices = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
        {
            return devices;
        }

        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            devices[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return devices;
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(_preferences, LogPackagePreferences.JsonOptions);
        await Api.Storage.WriteAsync(json, cancellationToken);
    }
}
