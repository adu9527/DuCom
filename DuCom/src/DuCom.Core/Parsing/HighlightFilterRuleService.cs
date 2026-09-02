using System.Text.Json;
using System.Text.Json.Serialization;
using DuCom.Core.Persistence;

namespace DuCom.Core.Parsing;

public sealed class HighlightFilterRuleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _filePath;

    public HighlightFilterRuleService(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        _filePath = filePath;
    }

    public IReadOnlyList<HighlightFilterRule> Load()
    {
        return LoadProjects().SelectMany(project => project.Rules).ToArray();
    }

    public IReadOnlyList<HighlightFilterRuleProject> LoadProjects()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            string json = File.ReadAllText(_filePath);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array &&
                document.RootElement.GetArrayLength() > 0 &&
                document.RootElement[0].TryGetProperty("rules", out _))
            {
                HighlightFilterRuleProjectDto[]? projects = JsonSerializer.Deserialize<HighlightFilterRuleProjectDto[]>(json, JsonOptions);
                return projects?.Select(ToProject).ToArray() ?? [];
            }

            HighlightFilterRuleDto[]? dtos = JsonSerializer.Deserialize<HighlightFilterRuleDto[]>(json, JsonOptions);
            return dtos is { Length: > 0 }
                ? [new HighlightFilterRuleProject(Guid.NewGuid(), "default", dtos.Select(ToModel).ToArray())]
                : [];
        }
        catch
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<HighlightFilterRule> rules)
    {
        SaveProjects([new HighlightFilterRuleProject(Guid.NewGuid(), "default", rules)]);
    }

    public void SaveProjects(IReadOnlyList<HighlightFilterRuleProject> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        AtomicFileStore.WriteAllText(_filePath, SerializeProjects(projects));
    }

    /// <summary>Serializes with the same shape <see cref="Save"/> writes, for staged commits.</summary>
    public static string Serialize(IReadOnlyList<HighlightFilterRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return SerializeProjects([new HighlightFilterRuleProject(Guid.NewGuid(), "default", rules)]);
    }

    public static string SerializeProjects(IReadOnlyList<HighlightFilterRuleProject> projects) =>
        JsonSerializer.Serialize(projects.Select(ToProjectDto).ToArray(), JsonOptions);

    private static HighlightFilterRule ToModel(HighlightFilterRuleDto dto)
    {
        return new HighlightFilterRule(
            string.IsNullOrEmpty(dto.Id) ? Guid.NewGuid() : Guid.Parse(dto.Id),
            dto.Name ?? string.Empty,
            dto.Kind,
            dto.Mode,
            dto.Pattern ?? string.Empty,
            dto.IsCaseSensitive,
            dto.IsEnabled,
            dto.ForegroundR,
            dto.ForegroundG,
            dto.ForegroundB,
            dto.BackgroundR,
            dto.BackgroundG,
            dto.BackgroundB,
            dto.Bold,
            dto.Italic);
    }

    private static HighlightFilterRuleDto ToDto(HighlightFilterRule rule)
    {
        return new HighlightFilterRuleDto
        {
            Id = rule.Id.ToString("D"),
            Name = rule.Name,
            Kind = rule.Kind,
            Mode = rule.Mode,
            Pattern = rule.Pattern,
            IsCaseSensitive = rule.IsCaseSensitive,
            IsEnabled = rule.IsEnabled,
            ForegroundR = rule.ForegroundR,
            ForegroundG = rule.ForegroundG,
            ForegroundB = rule.ForegroundB,
            BackgroundR = rule.BackgroundR,
            BackgroundG = rule.BackgroundG,
            BackgroundB = rule.BackgroundB,
            Bold = rule.Bold,
            Italic = rule.Italic,
        };
    }

    private static HighlightFilterRuleProject ToProject(HighlightFilterRuleProjectDto project) => new(
        string.IsNullOrEmpty(project.Id) ? Guid.NewGuid() : Guid.Parse(project.Id),
        project.Name ?? string.Empty,
        project.Rules?.Select(ToModel).ToArray() ?? []);

    private static HighlightFilterRuleProjectDto ToProjectDto(HighlightFilterRuleProject project) => new()
    {
        Id = project.Id.ToString("D"),
        Name = project.Name,
        Rules = project.Rules.Select(ToDto).ToArray(),
    };

    private sealed class HighlightFilterRuleProjectDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public HighlightFilterRuleDto[]? Rules { get; set; }
    }

    private sealed class HighlightFilterRuleDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public HighlightFilterRuleKind Kind { get; set; }

        public RuleMatchMode Mode { get; set; }

        public string? Pattern { get; set; }

        public bool IsCaseSensitive { get; set; }

        public bool IsEnabled { get; set; }

        public byte? ForegroundR { get; set; }

        public byte? ForegroundG { get; set; }

        public byte? ForegroundB { get; set; }

        public byte? BackgroundR { get; set; }

        public byte? BackgroundG { get; set; }

        public byte? BackgroundB { get; set; }

        public bool Bold { get; set; }

        public bool Italic { get; set; }
    }
}
