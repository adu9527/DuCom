using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DuCom.Core.Diagnostics;
using DuCom.Core.Persistence;

namespace DuCom.Services;

public sealed record VariablePlotSettings(
    int IngestionIntervalMs = 100,
    int RetentionSeconds = 300,
    int MaximumRawPointsPerSeries = 100_000,
    int MaximumRenderedPointsPerSeries = 3_000,
    VariableMonitorSamplingMode DefaultSamplingMode = VariableMonitorSamplingMode.EveryMatch,
    int DefaultSampleIntervalMs = 20,
    bool FollowLatest = true,
    int VisibleWindowSeconds = 30);

public sealed record VariableMonitorConfiguration(
    int SchemaVersion,
    VariablePlotSettings Plot,
    IReadOnlyList<VariableMonitorRule> Rules,
    IReadOnlyList<string> Diagnostics)
{
    public static VariableMonitorConfiguration Empty { get; } = new(2, new VariablePlotSettings(), [], []);
}

/// <summary>Versioned, tolerant JSON persistence for monitor rules and plot settings.</summary>
public static class VariableMonitorRuleStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "monitor-rules.json");

    public static IReadOnlyList<VariableMonitorRule> Load() => LoadConfiguration().Rules;

    public static VariableMonitorConfiguration LoadConfiguration(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path))
        {
            return VariableMonitorConfiguration.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            JsonElement rulesElement;
            int schemaVersion;
            VariablePlotSettings plot;
            if (root.ValueKind == JsonValueKind.Array)
            {
                schemaVersion = 1;
                plot = new VariablePlotSettings();
                rulesElement = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "rules", out rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
            {
                schemaVersion = TryGetProperty(root, "schemaVersion", out JsonElement version) && version.TryGetInt32(out int parsed) ? parsed : 2;
                plot = TryGetProperty(root, "plot", out JsonElement plotElement)
                    ? NormalizePlot(plotElement.Deserialize<VariablePlotSettings>(JsonOptions) ?? new VariablePlotSettings())
                    : new VariablePlotSettings();
            }
            else
            {
                return VariableMonitorConfiguration.Empty with { Diagnostics = ["Configuration root must be an array or an object containing a rules array."] };
            }

            List<VariableMonitorRule> rules = [];
            List<string> diagnostics = [];
            HashSet<Guid> ids = [];
            int index = 0;
            foreach (JsonElement element in rulesElement.EnumerateArray())
            {
                try
                {
                    VariableMonitorRule? rule = element.Deserialize<VariableMonitorRule>(JsonOptions);
                    if (rule is null)
                    {
                        diagnostics.Add($"rules[{index}] is null.");
                    }
                    else
                    {
                        rule = NormalizeRule(rule, index);
                        if (!ids.Add(rule.Id))
                        {
                            diagnostics.Add($"rules[{index}] has a duplicate id and was skipped.");
                        }
                        else if (string.IsNullOrWhiteSpace(rule.Pattern))
                        {
                            diagnostics.Add($"rules[{index}] has no pattern and was skipped.");
                        }
                        else
                        {
                            rules.Add(rule);
                        }
                    }
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
                {
                    diagnostics.Add($"rules[{index}] was skipped: {exception.Message}");
                }

                index++;
            }

            return new VariableMonitorConfiguration(schemaVersion, plot, rules.AsReadOnly(), diagnostics.AsReadOnly());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Program.DiagnosticLog?.Warning($"Failed to load monitor rules from {path}.", exception);
            return VariableMonitorConfiguration.Empty with { Diagnostics = [exception.Message] };
        }
    }

    public static void Save(IReadOnlyList<VariableMonitorRule> rules) =>
        Save(new VariableMonitorConfiguration(2, new VariablePlotSettings(), rules, []));

    public static void Save(VariableMonitorConfiguration configuration, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        path ??= FilePath;
        VariableMonitorConfiguration normalized = new(
            2,
            NormalizePlot(configuration.Plot),
            configuration.Rules.Select(NormalizeRule).ToArray(),
            []);
        try
        {
            AtomicFileStore.WriteAllText(path, JsonSerializer.Serialize(normalized, JsonOptions));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save monitor rules to {path}.", exception);
        }
    }

    public static VariableMonitorConfiguration CreateDefaultTemplates() => new(
        2,
        new VariablePlotSettings(),
        [
            Rule("Touch X", @"touch:\s*x=(?<value>-?\d+(?:\.\d+)?)", "px", "touch", "#42A5F5", 0),
            Rule("Touch Y", @"touch:.*?y=(?<value>-?\d+(?:\.\d+)?)", "px", "touch", "#EF5350", 1),
            Rule("Touch Pressure", @"touch:.*?pressure=(?<value>-?\d+(?:\.\d+)?)", null, "pressure", "#AB47BC", 2),
            Rule("Gyro X", @"gyro:\s*x=(?<value>-?\d+(?:\.\d+)?)", "dps", "gyro", "#42A5F5", 3),
            Rule("Gyro Y", @"gyro:.*?y=(?<value>-?\d+(?:\.\d+)?)", "dps", "gyro", "#66BB6A", 4),
            Rule("Gyro Z", @"gyro:.*?z=(?<value>-?\d+(?:\.\d+)?)", "dps", "gyro", "#FFA726", 5),
            Rule("Light Lux", @"light:\s*lux=(?<value>-?\d+(?:\.\d+)?)", "lux", "light", "#FFCA28", 6,
                VariableMonitorSamplingMode.Latest, 100),
        ],
        []);

    public static void ResetDefaults(string? path = null) => Save(CreateDefaultTemplates(), path);

    private static VariableMonitorRule Rule(string name, string pattern, string? unit, string axis, string color, int order,
        VariableMonitorSamplingMode sampling = VariableMonitorSamplingMode.EveryMatch, int interval = 20) =>
        new(Guid.NewGuid(), name, null, pattern, true, order, VariableMonitorValueType.Number, "value", 1, 0,
            unit, true, color, axis, sampling, interval);

    private static VariablePlotSettings NormalizePlot(VariablePlotSettings value) => value with
    {
        IngestionIntervalMs = Math.Clamp(value.IngestionIntervalMs, 50, 1_000),
        RetentionSeconds = Math.Clamp(value.RetentionSeconds, 1, 86_400),
        MaximumRawPointsPerSeries = Math.Clamp(value.MaximumRawPointsPerSeries, 100, 1_000_000),
        MaximumRenderedPointsPerSeries = Math.Clamp(value.MaximumRenderedPointsPerSeries, 100, 20_000),
        DefaultSampleIntervalMs = Math.Clamp(value.DefaultSampleIntervalMs, 1, 60_000),
        VisibleWindowSeconds = Math.Clamp(value.VisibleWindowSeconds, 1, 86_400),
    };

    private static VariableMonitorRule NormalizeRule(VariableMonitorRule rule, int order = 0) => rule with
    {
        Id = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id,
        Name = string.IsNullOrWhiteSpace(rule.Name) ? $"Variable {order + 1}" : rule.Name.Trim(),
        PortName = string.IsNullOrWhiteSpace(rule.PortName) ? null : rule.PortName.Trim(),
        Scale = double.IsFinite(rule.Scale) ? rule.Scale : 1,
        Offset = double.IsFinite(rule.Offset) ? rule.Offset : 0,
        Color = NormalizeColor(rule.Color),
        AxisId = string.IsNullOrWhiteSpace(rule.AxisId) ? "default" : rule.AxisId.Trim(),
        SampleIntervalMs = Math.Clamp(rule.SampleIntervalMs, 1, 60_000),
    };

    private static string NormalizeColor(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit) ? color.ToUpperInvariant() : "#42A5F5";

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
