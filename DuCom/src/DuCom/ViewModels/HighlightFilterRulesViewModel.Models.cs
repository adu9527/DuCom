using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Parsing;

namespace DuCom.ViewModels;

public partial class HighlightFilterRulesViewModel
{
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
