using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class HighlightFilterRulesViewModel
{
    [RelayCommand]
    private void AddRule()
    {
        var rule = new HighlightFilterRule(
            Guid.NewGuid(),
            GetResourceString("HighlightFilter.NewRuleName"),
            HighlightFilterRuleKind.Highlight,
            RuleMatchMode.Contains,
            string.Empty,
            false,
            true,
            0xFF,
            0xFF,
            0xFF,
            null,
            null,
            null);
        Rules.Add(new RuleEditor(rule));
        SelectedIndex = Rules.Count - 1;
        MarkChanged();
    }

    [RelayCommand]
    private void AddProject()
    {
        CommitProjectRules();
        Projects.Add(new RuleProjectEditor(Guid.NewGuid(), NextProjectName(), []));
        SelectedProjectIndex = Projects.Count - 1;
        MarkChanged();
        PublishProjectsChanged();
    }

    [RelayCommand]
    private void CopyDefaultProject()
    {
        CommitProjectRules();
        RuleProjectEditor? source = Projects.FirstOrDefault(project =>
            string.Equals(project.Name, "default", StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            IReadOnlyList<HighlightFilterRule> defaultRules = DefaultDuComData.MergeHighlightRules([], out _);
            source = new RuleProjectEditor(Guid.NewGuid(), "default", defaultRules);
            Projects.Insert(0, source);
        }

        HighlightFilterRule[] copiedRules = [.. source.Rules.Select(rule => rule with { Id = Guid.NewGuid() })];
        Projects.Add(new RuleProjectEditor(Guid.NewGuid(), NextProjectName(), copiedRules));
        SelectedProjectIndex = Projects.Count - 1;
        MarkChanged();
        PublishProjectsChanged();
    }

    [RelayCommand]
    private void DeleteProject()
    {
        if (!IsProjectSelected)
        {
            return;
        }

        int index = SelectedProjectIndex;
        _loadedProjectIndex = -1;
        Projects.RemoveAt(index);
        SelectedProjectIndex = Projects.Count == 0 ? -1 : Math.Min(index, Projects.Count - 1);
        MarkChanged();
        PublishProjectsChanged();
    }

    [RelayCommand(CanExecute = nameof(IsRuleSelected))]
    private void DeleteRule()
    {
        if (!IsRuleSelected)
        {
            return;
        }

        int index = SelectedIndex;
        Rules.RemoveAt(index);
        SelectedIndex = Math.Min(index, Rules.Count - 1);
        MarkChanged();
    }

    [RelayCommand]
    private void DeleteRuleItem(RuleEditor? rule)
    {
        if (rule is null)
        {
            return;
        }

        int index = Rules.IndexOf(rule);
        if (index < 0)
        {
            return;
        }

        Rules.RemoveAt(index);
        SelectedIndex = Rules.Count == 0 ? -1 : Math.Min(index, Rules.Count - 1);
        MarkChanged();
    }

    [RelayCommand(CanExecute = nameof(IsRuleSelected))]
    private void MoveUp()
    {
        if (SelectedIndex <= 0)
        {
            return;
        }

        (Rules[SelectedIndex], Rules[SelectedIndex - 1]) = (Rules[SelectedIndex - 1], Rules[SelectedIndex]);
        SelectedIndex--;
        MarkChanged();
    }

    [RelayCommand(CanExecute = nameof(IsRuleSelected))]
    private void MoveDown()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Rules.Count - 1)
        {
            return;
        }

        (Rules[SelectedIndex], Rules[SelectedIndex + 1]) = (Rules[SelectedIndex + 1], Rules[SelectedIndex]);
        SelectedIndex++;
        MarkChanged();
    }

    [RelayCommand]
    private void Save()
    {
        CommitProjectRules();
        if (Projects.Count == 0)
        {
            ErrorMessage = GetResourceString("HighlightFilter.Error.NoProject");
            return;
        }

        foreach (RuleProjectEditor project in Projects)
        {
            for (int index = 0; index < project.Rules.Count; index++)
            {
                RuleValidationResult result = HighlightFilterRuleValidation.Validate(project.Rules[index]);
                if (!result.IsValid)
                {
                    SelectedProjectIndex = Projects.IndexOf(project);
                    SelectedIndex = index;
                    ErrorMessage = GetResourceString(result.ErrorKey!);
                    return;
                }
            }
        }

        try
        {
            _service.SaveProjects(Projects.Select(project => project.ToModel()).ToArray());
            ErrorMessage = GetResourceString("HighlightFilter.SaveSuccess");
            ChangeStatus = GetResourceString("HighlightFilter.Status.Saved");
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"{GetResourceString("HighlightFilter.SaveFailed")}: {exception.Message}";
        }
    }

    [RelayCommand]
    private void Apply()
    {
        CommitProjectRules();
        if (!IsProjectSelected)
        {
            ErrorMessage = GetResourceString("HighlightFilter.Error.NoProject");
            return;
        }

        RuleProjectEditor selected = Projects[SelectedProjectIndex];
        for (int index = 0; index < selected.Rules.Count; index++)
        {
            RuleValidationResult result = HighlightFilterRuleValidation.Validate(selected.Rules[index]);
            if (!result.IsValid)
            {
                SelectedIndex = index;
                ErrorMessage = GetResourceString(result.ErrorKey!);
                return;
            }
        }

        HighlightFilterRuleProject[] projects = [.. Projects.Select(project => project.ToModel())];
        Applied?.Invoke(this, new HighlightRulesAppliedEventArgs(projects, selected.Id));
        ErrorMessage = GetResourceString("HighlightFilter.ApplySuccess");
        ChangeStatus = GetResourceString("HighlightFilter.Status.AppliedNotSaved");
    }

    [RelayCommand]
    private void Reset()
    {
        CommitProjectRules();
        IReadOnlyList<HighlightFilterRule> defaultRules = DefaultDuComData.MergeHighlightRules([], out _);
        int defaultIndex = -1;
        for (int index = 0; index < Projects.Count; index++)
        {
            if (!string.Equals(Projects[index].Name, "default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Projects[index].Name = "default";
            Projects[index].Rules = RestoreDefaultRules(Projects[index].Rules, defaultRules);
            defaultIndex = index;
            break;
        }

        if (defaultIndex < 0)
        {
            Projects.Insert(0, new RuleProjectEditor(Guid.NewGuid(), "default", defaultRules));
            defaultIndex = 0;
        }

        // Prevent the old in-memory Rules collection from being committed over the freshly
        // restored default project while forcing the selection callbacks to reload it.
        _loadedProjectIndex = -1;
        SelectedProjectIndex = -1;
        SelectedProjectIndex = defaultIndex;
        ErrorMessage = string.Empty;
        MarkChanged();
        PublishProjectsChanged();
    }

    [RelayCommand]
    private void ToggleRuleEnabled(RuleEditor? rule)
    {
        if (rule is null)
        {
            return;
        }

        rule.Update(rule.Model with { IsEnabled = !rule.Model.IsEnabled });
        if (Rules.IndexOf(rule) == SelectedIndex)
        {
            _isLoadingSelection = true;
            EditingIsEnabled = rule.Model.IsEnabled;
            _isLoadingSelection = false;
        }
        MarkChanged();
    }

    [RelayCommand]
    private void DuplicateRule(RuleEditor? rule)
    {
        if (rule is null)
        {
            return;
        }

        HighlightFilterRule copy = rule.Model with { Id = Guid.NewGuid(), Name = NextRuleName(rule.Name) };
        int index = Rules.IndexOf(rule) + 1;
        Rules.Insert(index, new RuleEditor(copy));
        SelectedIndex = index;
        MarkChanged();
    }
}
