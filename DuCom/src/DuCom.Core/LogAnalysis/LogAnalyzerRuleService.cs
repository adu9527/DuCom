using System.Reflection;
using System.Text.Json;
using DuCom.Core.Persistence;

namespace DuCom.Core.LogAnalysis;

public sealed class LogAnalyzerRuleService(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IReadOnlyList<LogAnalyzerRule> Load()
    {
        if (!File.Exists(filePath))
        {
            IReadOnlyList<LogAnalyzerRule> defaults = LoadDefaults();
            Save(defaults);
            return defaults;
        }

        try
        {
            return (JsonSerializer.Deserialize<LogAnalyzerRule[]>(File.ReadAllText(filePath), JsonOptions) ?? [])
                .Select(rule => rule with
                {
                    Comment = string.IsNullOrWhiteSpace(rule.Comment) ? rule.Name : rule.Comment,
                })
                .ToArray();
        }
        catch
        {
            return LoadDefaults();
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
}
