using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class HighlightFilterRulesViewModel : ObservableObject
{
    private readonly HighlightFilterRuleService _service;
    private bool _isLoadingSelection;
    private bool _isLoadingProjectSelection;
    private int _loadedProjectIndex = -1;

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

    public ObservableCollection<RuleProjectEditor> Projects { get; } = [];

    public ObservableCollection<RuleEditor> Rules { get; } = [];

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

    public IReadOnlyList<RuleKindOption> KindOptions { get; } =
    [
        new(HighlightFilterRuleKind.Highlight, GetResourceString("HighlightFilter.Kind.Highlight")),
        new(HighlightFilterRuleKind.Filter, GetResourceString("HighlightFilter.Kind.Filter")),
    ];

    public IReadOnlyList<MatchModeOption> ModeOptions { get; } =
    [
        new(RuleMatchMode.Regex, GetResourceString("HighlightFilter.Mode.Regex")),
        new(RuleMatchMode.Contains, GetResourceString("HighlightFilter.Mode.Contains")),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRuleSelected))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRuleCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial int SelectedIndex { get; set; } = -1;

    public bool IsRuleSelected => SelectedIndex >= 0 && SelectedIndex < Rules.Count;

    [ObservableProperty]
    public partial string EditingName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial HighlightFilterRuleKind EditingKind { get; set; }

    public RuleKindOption? SelectedKindOption
    {
        get => KindOptions.FirstOrDefault(option => option.Kind == EditingKind);
        set
        {
            if (value is not null)
            {
                EditingKind = value.Kind;
            }
        }
    }

    [ObservableProperty]
    public partial RuleMatchMode EditingMode { get; set; }

    public MatchModeOption? SelectedModeOption
    {
        get => ModeOptions.FirstOrDefault(option => option.Mode == EditingMode);
        set
        {
            if (value is not null)
            {
                EditingMode = value.Mode;
            }
        }
    }

    [ObservableProperty]
    public partial string EditingPattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EditingIsCaseSensitive { get; set; }

    [ObservableProperty]
    public partial bool EditingIsEnabled { get; set; }

    [ObservableProperty]
    public partial string EditingForegroundHex { get; set; } = "#FFFFFF";

    [ObservableProperty]
    public partial string EditingBackgroundHex { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EditingBold { get; set; }

    [ObservableProperty]
    public partial bool EditingItalic { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    partial void OnSelectedIndexChanged(int value)
    {
        _isLoadingSelection = true;
        try
        {
            if (IsRuleSelected)
            {
                HighlightFilterRule rule = Rules[value].Model;
                EditingName = rule.Name;
                EditingKind = rule.Kind;
                EditingMode = rule.Mode;
                EditingPattern = rule.Pattern;
                EditingIsCaseSensitive = rule.IsCaseSensitive;
                EditingIsEnabled = rule.IsEnabled;
                EditingForegroundHex = ToHex(rule.ForegroundR, rule.ForegroundG, rule.ForegroundB);
                EditingBackgroundHex = ToHex(rule.BackgroundR, rule.BackgroundG, rule.BackgroundB);
                EditingBold = rule.Bold;
                EditingItalic = rule.Italic;
            }
            else
            {
                EditingName = string.Empty;
                EditingKind = HighlightFilterRuleKind.Highlight;
                EditingMode = RuleMatchMode.Contains;
                EditingPattern = string.Empty;
                EditingIsCaseSensitive = false;
                EditingIsEnabled = true;
                EditingForegroundHex = "#FFFFFF";
                EditingBackgroundHex = string.Empty;
                EditingBold = false;
                EditingItalic = false;
            }

            ErrorMessage = string.Empty;
            OnPropertyChanged(nameof(PreviewRuns));
        }
        finally
        {
            _isLoadingSelection = false;
        }
    }

    partial void OnEditingNameChanged(string value) => CommitEdit();

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

    partial void OnEditingKindChanged(HighlightFilterRuleKind value)
    {
        OnPropertyChanged(nameof(SelectedKindOption));
        CommitEdit();
    }

    partial void OnEditingModeChanged(RuleMatchMode value)
    {
        OnPropertyChanged(nameof(SelectedModeOption));
        CommitEdit();
    }

    partial void OnEditingPatternChanged(string value) => CommitEdit();

    partial void OnEditingIsCaseSensitiveChanged(bool value) => CommitEdit();

    partial void OnEditingIsEnabledChanged(bool value) => CommitEdit();

    partial void OnEditingForegroundHexChanged(string value) => CommitEdit();

    partial void OnEditingBackgroundHexChanged(string value) => CommitEdit();
    partial void OnEditingBoldChanged(bool value) => CommitEdit();
    partial void OnEditingItalicChanged(bool value) => CommitEdit();

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

    private void CommitEdit()
    {
        if (_isLoadingSelection || !IsRuleSelected)
        {
            return;
        }

        RuleEditor editor = Rules[SelectedIndex];
        editor.Update(CreateRuleFromEditing(editor.Model.Id));
        OnPropertyChanged(nameof(PreviewRuns));
        MarkChanged();
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

    private string NextRuleName(string baseName)
    {
        HashSet<string> names = new(Rules.Select(rule => rule.Name), StringComparer.OrdinalIgnoreCase);
        for (int suffix = 1; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void MarkChanged() => ChangeStatus = GetResourceString("HighlightFilter.Status.Unsaved");

    private void CommitProjectRules()
    {
        if (_loadedProjectIndex >= 0 && _loadedProjectIndex < Projects.Count)
        {
            Projects[_loadedProjectIndex].Rules = [.. Rules.Select(rule => rule.Model)];
        }
    }

    public sealed partial class RuleEditor : ObservableObject
    {
        public RuleEditor(HighlightFilterRule model)
        {
            Model = model;
            Name = model.Name;
            Kind = model.Kind;
            IsEnabled = model.IsEnabled;
        }

        public HighlightFilterRule Model { get; private set; }

        [ObservableProperty]
        public partial string Name { get; private set; }

        [ObservableProperty]
        public partial HighlightFilterRuleKind Kind { get; private set; }

        [ObservableProperty]
        public partial bool IsEnabled { get; private set; }

        public void Update(HighlightFilterRule model)
        {
            Model = model;
            Name = model.Name;
            Kind = model.Kind;
            IsEnabled = model.IsEnabled;
        }
    }

    public sealed partial class RuleProjectEditor : ObservableObject
    {
        public RuleProjectEditor(HighlightFilterRuleProject project)
            : this(project.Id, project.Name, project.Rules)
        {
        }

        public RuleProjectEditor(Guid id, string name, IReadOnlyList<HighlightFilterRule> rules)
        {
            Id = id;
            Name = name;
            Rules = rules;
        }

        public Guid Id { get; }

        [ObservableProperty]
        public partial string Name { get; set; }

        public IReadOnlyList<HighlightFilterRule> Rules { get; set; }

        public HighlightFilterRuleProject ToModel() => new(Id, Name, Rules);
    }

    public sealed record RuleKindOption(HighlightFilterRuleKind Kind, string DisplayName);

    public sealed record MatchModeOption(RuleMatchMode Mode, string DisplayName);

    private HighlightFilterRule CreateRuleFromEditing(Guid id)
    {
        _ = TryParseHex(EditingForegroundHex, out byte? foregroundR, out byte? foregroundG, out byte? foregroundB);
        HighlightFilterRule existing = Rules[SelectedIndex].Model;

        return new HighlightFilterRule(
            id,
            EditingName,
            EditingKind,
            EditingMode,
            EditingPattern,
            EditingIsCaseSensitive,
            EditingIsEnabled,
            foregroundR,
            foregroundG,
            foregroundB,
            existing.BackgroundR,
            existing.BackgroundG,
            existing.BackgroundB,
            EditingBold,
            EditingItalic);
    }

    private static string ToHex(byte? r, byte? g, byte? b)
    {
        if (!r.HasValue || !g.HasValue || !b.HasValue)
        {
            return string.Empty;
        }

        return $"#{r.Value:X2}{g.Value:X2}{b.Value:X2}";
    }

    private static bool TryParseHex(string hex, out byte? r, out byte? g, out byte? b)
    {
        r = null;
        g = null;
        b = null;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length == 3)
        {
            if (!byte.TryParse(span[..1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte rValue) ||
                !byte.TryParse(span[1..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte gValue) ||
                !byte.TryParse(span[2..3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte bValue))
            {
                return false;
            }

            r = (byte)(rValue * 16 + rValue);
            g = (byte)(gValue * 16 + gValue);
            b = (byte)(bValue * 16 + bValue);
            return true;
        }

        if (span.Length == 6 &&
            byte.TryParse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r6) &&
            byte.TryParse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g6) &&
            byte.TryParse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b6))
        {
            r = r6;
            g = g6;
            b = b6;
            return true;
        }

        return false;
    }

    private static string GetResourceString(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;
}

public sealed class HighlightRulesAppliedEventArgs(
    IReadOnlyList<HighlightFilterRuleProject> projects,
    Guid selectedProjectId) : EventArgs
{
    public IReadOnlyList<HighlightFilterRuleProject> Projects { get; } = projects;

    public Guid SelectedProjectId { get; } = selectedProjectId;
}

public sealed class HighlightRuleProjectsChangedEventArgs(
    IReadOnlyList<HighlightFilterRuleProject> projects) : EventArgs
{
    public IReadOnlyList<HighlightFilterRuleProject> Projects { get; } = projects;
}
