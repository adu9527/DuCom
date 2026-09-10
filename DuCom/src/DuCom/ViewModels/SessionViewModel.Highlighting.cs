using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Parsing;
using DuCom.Core.Sessions;
using DuCom.Core.Storage;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class SessionViewModel
{
    private bool _regexTimeoutReported;

    public ObservableCollection<HighlightFilterRuleProject> HighlightRuleProjects { get; } = [];

    public IReadOnlyList<HighlightFilterRule> HighlightFilterRules =>
        HighlightRuleProjects.FirstOrDefault(project => project.Id == HighlightRuleProjectId)?.Rules ?? [];

    [ObservableProperty]
    public partial Guid? HighlightRuleProjectId { get; set; }

    partial void OnHighlightRuleProjectIdChanged(Guid? value)
    {
        ResetHighlightProjection();
    }

    public void ApplyHighlightRuleProject(Guid? projectId)
    {
        if (HighlightRuleProjectId == projectId)
        {
            ResetHighlightProjection();
            return;
        }

        HighlightRuleProjectId = projectId;
    }

    private void ResetHighlightProjection()
    {
        _projector.Reset();
        VisibleLines.Clear();
        _visibleCharacterCount = 0;
        _visibleSearchSnapshotDirty = true;
        _renderedLastLogicalId = null;
        _renderedLastSegmentIndex = -1;
    }

    public void ReplaceHighlightRuleProjects(IReadOnlyList<HighlightFilterRuleProject> projects)
    {
        Guid? previousProjectId = HighlightRuleProjectId;
        HighlightRuleProjects.Clear();
        foreach (HighlightFilterRuleProject project in projects)
        {
            HighlightRuleProjects.Add(project);
        }

        Guid? nextProjectId = previousProjectId is null
            ? null
            : HighlightRuleProjects.Any(project => project.Id == previousProjectId)
                ? previousProjectId
                : HighlightRuleProjects.FirstOrDefault(project => string.Equals(project.Name, "default", StringComparison.OrdinalIgnoreCase))?.Id
                    ?? HighlightRuleProjects.FirstOrDefault()?.Id;
        ApplyHighlightRuleProject(nextProjectId);
    }

    [ObservableProperty]
    public partial bool FilterEnabled { get; set; } = true;

    private void ReportRegexTimeout()
    {
        if (_regexTimeoutReported)
        {
            return;
        }

        _regexTimeoutReported = true;
        Program.DiagnosticLog?.Warning($"Highlight/filter regex timeout. Port={PortName}; Timeout={HighlightFilterRuleMatcher.MatchTimeout.TotalMilliseconds}ms");
        OnSessionWarning(this, new SessionWarningEventArgs(GetResourceString("Log.RegexTimeout")));
    }
}
