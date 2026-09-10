using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Parsing;

namespace DuCom.ViewModels;

public partial class HighlightFilterRulesViewModel
{
    private bool _isLoadingProjectSelection;
    private int _loadedProjectIndex = -1;

    public ObservableCollection<RuleProjectEditor> Projects { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectSelected))]
    public partial int SelectedProjectIndex { get; set; } = -1;

    public bool IsProjectSelected => SelectedProjectIndex >= 0 && SelectedProjectIndex < Projects.Count;

    [ObservableProperty]
    public partial string EditingProjectName { get; set; } = string.Empty;

    partial void OnSelectedProjectIndexChanged(int value)
    {
        CommitProjectRules();
        _loadedProjectIndex = value;
        _isLoadingProjectSelection = true;
        try
        {
            Rules.Clear();
            if (value >= 0 && value < Projects.Count)
            {
                EditingProjectName = Projects[value].Name;
                foreach (HighlightFilterRule rule in Projects[value].Rules)
                {
                    Rules.Add(new RuleEditor(rule));
                }
            }
            else
            {
                EditingProjectName = string.Empty;
            }

            SelectedIndex = Rules.Count > 0 ? 0 : -1;
        }
        finally
        {
            _isLoadingProjectSelection = false;
        }
    }

    partial void OnEditingProjectNameChanged(string value)
    {
        if (_isLoadingProjectSelection || !IsProjectSelected)
        {
            return;
        }

        Projects[SelectedProjectIndex].Name = value;
        MarkChanged();
        PublishProjectsChanged();
    }

    private string NextProjectName()
    {
        HashSet<string> names = new(Projects.Select(project => project.Name), StringComparer.OrdinalIgnoreCase);
        for (int suffix = 1; ; suffix++)
        {
            string candidate = $"default{suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void PublishProjectsChanged()
    {
        CommitProjectRules();
        ProjectsChanged?.Invoke(
            this,
            new HighlightRuleProjectsChangedEventArgs(
                [.. Projects.Select(project => project.ToModel())]));
    }

    private static List<HighlightFilterRule> RestoreDefaultRules(
        IReadOnlyList<HighlightFilterRule> currentRules,
        IReadOnlyList<HighlightFilterRule> defaultRules)
    {
        Dictionary<string, HighlightFilterRule> currentByName = currentRules
            .GroupBy(rule => rule.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        HashSet<string> defaultNames = new(defaultRules.Select(rule => rule.Name), StringComparer.Ordinal);
        List<HighlightFilterRule> restored = [];
        foreach (HighlightFilterRule defaultRule in defaultRules)
        {
            restored.Add(currentByName.TryGetValue(defaultRule.Name, out HighlightFilterRule current)
                ? defaultRule with { Id = current.Id }
                : defaultRule);
        }

        restored.AddRange(currentRules.Where(rule => !defaultNames.Contains(rule.Name)));
        return restored;
    }

    private void CommitProjectRules()
    {
        if (_loadedProjectIndex >= 0 && _loadedProjectIndex < Projects.Count)
        {
            Projects[_loadedProjectIndex].Rules = [.. Rules.Select(rule => rule.Model)];
        }
    }
}
