using System.Reflection;
using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Core.LogAnalysis;

public sealed class LogAnalyzerRuleService(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IReadOnlyList<LogAnalyzerRule> Load()
    {
        IReadOnlyList<LogAnalyzerRule> defaults = LoadDefaults();
        if (!File.Exists(filePath))
        {
            Save(defaults);
            return defaults;
        }

        try
        {
            LogAnalyzerRule[] userRules = (JsonSerializer.Deserialize<LogAnalyzerRule[]>(File.ReadAllText(filePath), JsonOptions) ?? [])
                .Select(rule => rule with
                {
                    Comment = string.IsNullOrWhiteSpace(rule.Comment) ? rule.Name : rule.Comment,
                })
                .ToArray();
            IReadOnlyList<LogAnalyzerRule> merged = MergeDefaults(userRules, defaults);
            if (!userRules.SequenceEqual(merged)) Save(merged);
            return merged;
        }
        catch
        {
            Save(defaults);
            return defaults;
        }
    }

    public void Save(IReadOnlyList<LogAnalyzerRule> rules) =>
        AtomicFileStore.WriteAllText(filePath, JsonSerializer.Serialize(rules, JsonOptions));

    public IReadOnlyList<LogAnalyzerRule> Import(string xmlPath)
    {
        using FileStream stream = File.OpenRead(xmlPath);
        IReadOnlyList<LogAnalyzerRule> rules = AnalyseDocRuleImporter.Import(stream);
        Save(rules);
        return rules;
    }

    public IReadOnlyList<LogAnalyzerRule> Reset()
    {
        IReadOnlyList<LogAnalyzerRule> rules = LoadDefaults();
        Save(rules);
        return rules;
    }

    public static IReadOnlyList<LogAnalyzerRule> LoadDefaults()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DuCom.Core.LogAnalysis.bes_ibrt.xml")
            ?? throw new InvalidOperationException("The built-in BES analyzer rule pack is missing.");
        return AnalyseDocRuleImporter.Import(stream);
    }

    private static List<LogAnalyzerRule> MergeDefaults(
        LogAnalyzerRule[] userRules,
        IReadOnlyList<LogAnalyzerRule> defaults)
    {
        Dictionary<string, LogAnalyzerRule> defaultsByPattern = defaults
            .GroupBy(rule => rule.Pattern, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> userPatterns = new(StringComparer.OrdinalIgnoreCase);
        List<LogAnalyzerRule> merged = new(userRules.Length + defaults.Count);

        foreach (LogAnalyzerRule userRule in userRules)
        {
            if (!userPatterns.Add(userRule.Pattern))
            {
                merged.Add(userRule);
                continue;
            }

            if (!defaultsByPattern.TryGetValue(userRule.Pattern, out LogAnalyzerRule? defaultRule))
            {
                merged.Add(userRule);
                continue;
            }

            merged.Add(defaultRule with
            {
                Id = userRule.Id,
                IsEnabled = userRule.IsEnabled,
                IsCaseSensitive = userRule.IsCaseSensitive,
            });
        }

        foreach (LogAnalyzerRule defaultRule in defaults)
        {
            if (userPatterns.Add(defaultRule.Pattern)) merged.Add(defaultRule);
        }

        return merged;
    }
}
