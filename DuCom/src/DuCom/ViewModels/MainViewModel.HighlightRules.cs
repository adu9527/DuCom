using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private void OnHighlightRulesApplied(object? sender, HighlightRulesAppliedEventArgs e)
    {
        SessionViewModel? session = _activeLogSession ?? SelectedSession ?? SelectedRightSession;
        if (session is null)
        {
            StatusMessage = GetResourceString("Status.NoSessionSelected");
            return;
        }

        session.ReplaceHighlightRuleProjects(e.Projects);
        session.ApplyHighlightRuleProject(e.SelectedProjectId);
        RememberSessionHighlightProject(session);
        StatusMessage = GetResourceString("HighlightFilter.ApplySuccess");
    }

    private void OnHighlightRuleProjectsChanged(object? sender, HighlightRuleProjectsChangedEventArgs e)
    {
        HighlightRuleProjects.Clear();
        foreach (HighlightFilterRuleProject project in e.Projects)
        {
            HighlightRuleProjects.Add(project);
        }

        HighlightFilterRules.Clear();
        foreach (HighlightFilterRule rule in e.Projects.SelectMany(project => project.Rules))
        {
            HighlightFilterRules.Add(rule);
        }

        foreach (SessionViewModel session in Sessions)
        {
            Guid? previousProjectId = session.HighlightRuleProjectId;
            session.ReplaceHighlightRuleProjects(e.Projects);
            if (previousProjectId != session.HighlightRuleProjectId)
            {
                RememberSessionHighlightProject(session);
            }
        }
    }

    [RelayCommand]
    private void ShowHighlightFilterRules()
    {
        var window = new HighlightFilterRulesWindow(new HighlightFilterRuleService(HighlightFilterRulesFilePath))
        {
            Owner = Application.Current.MainWindow,
        };
        window.Closed += (_, _) => LoadHighlightFilterRules();
        window.Show();
    }

    private void LoadHighlightFilterRules()
    {
        try
        {
            var service = new HighlightFilterRuleService(HighlightFilterRulesFilePath);
            List<HighlightFilterRuleProject> projects = [.. service.LoadProjects()];
            if (projects.Count == 0)
            {
                projects.Add(new HighlightFilterRuleProject(Guid.NewGuid(), "default", DefaultDuComData.MergeHighlightRules([], out _)));
                service.SaveProjects(projects);
            }
            else if (projects.Count == 1 &&
                     projects[0].Name is "BES Default" or "Imported rules" or "Rules")
            {
                projects[0] = projects[0] with { Name = "default" };
                service.SaveProjects(projects);
            }
            else
            {
                bool repaired = false;
                for (int index = 0; index < projects.Count; index++)
                {
                    if (!string.Equals(projects[index].Name, "default", StringComparison.OrdinalIgnoreCase) ||
                        projects[index].Rules.Count > 0)
                    {
                        continue;
                    }

                    projects[index] = projects[index] with
                    {
                        Name = "default",
                        Rules = DefaultDuComData.MergeHighlightRules([], out _),
                    };
                    repaired = true;
                }

                if (repaired)
                {
                    service.SaveProjects(projects);
                    Program.DiagnosticLog?.Information("Repaired empty default highlight-rule project.");
                }
            }

            HighlightRuleProjects.Clear();
            foreach (HighlightFilterRuleProject project in projects)
            {
                HighlightRuleProjects.Add(project);
            }
            HighlightFilterRules.Clear();
            foreach (HighlightFilterRule rule in projects.SelectMany(project => project.Rules))
            {
                HighlightFilterRules.Add(rule);
            }

            foreach (SessionViewModel session in Sessions)
            {
                session.ReplaceHighlightRuleProjects(HighlightRuleProjects);
            }

            Program.DiagnosticLog?.Information($"Loaded {HighlightRuleProjects.Count} highlight rule projects and {HighlightFilterRules.Count} rules from {HighlightFilterRulesFilePath}.");
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to load highlight/filter rules.", exception);
        }
    }
}
