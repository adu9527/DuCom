using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Parsing;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class HighlightFilterRulesViewModel : ObservableObject
{
    private readonly HighlightFilterRuleService _service;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewRuns))]
    public partial string PreviewText { get; set; } = "ERROR Device boot failed";

    public IReadOnlyList<StyleRun> PreviewRuns
    {
        get
        {
            if (!IsRuleSelected || string.IsNullOrEmpty(PreviewText))
            {
                return [new StyleRun(PreviewText, null, null, null, null, null, null, false, false, false)];
            }

            HighlightFilterRule rule = Rules[SelectedIndex].Model;
            return [new StyleRun(
                PreviewText,
                rule.ForegroundR,
                rule.ForegroundG,
                rule.ForegroundB,
                null,
                null,
                null,
                rule.Bold,
                false,
                false,
                rule.Italic)];
        }
    }

    [ObservableProperty]
    public partial string ChangeStatus { get; private set; } = string.Empty;

    public event EventHandler? Saved;

    public event EventHandler<HighlightRulesAppliedEventArgs>? Applied;

    public event EventHandler<HighlightRuleProjectsChangedEventArgs>? ProjectsChanged;

    public HighlightFilterRulesViewModel(HighlightFilterRuleService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        List<HighlightFilterRuleProject> projects = [.. service.LoadProjects()];
        if (projects.Count == 0)
        {
            projects.Add(new HighlightFilterRuleProject(
                Guid.NewGuid(),
                "default",
                DefaultDuComData.MergeHighlightRules([], out _)));
            service.SaveProjects(projects);
        }

        foreach (HighlightFilterRuleProject project in projects)
        {
            IReadOnlyList<HighlightFilterRule> rules =
                string.Equals(project.Name, "default", StringComparison.OrdinalIgnoreCase) && project.Rules.Count == 0
                    ? DefaultDuComData.MergeHighlightRules([], out _)
                    : project.Rules;
            Projects.Add(new RuleProjectEditor(project.Id, project.Name, rules));
        }

        SelectedProjectIndex = Projects.Count > 0 ? 0 : -1;
    }

    private void MarkChanged() => ChangeStatus = GetResourceString("HighlightFilter.Status.Unsaved");

    private static string GetResourceString(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;
}
