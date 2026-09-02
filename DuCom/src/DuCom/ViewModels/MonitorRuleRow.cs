using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Diagnostics;

namespace DuCom.ViewModels;

public partial class MonitorRuleRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PortName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Pattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int Order { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public VariableMonitorRule ToRule() => new(Id, Name, string.IsNullOrWhiteSpace(PortName) ? null : PortName, Pattern, IsEnabled, Order);

    public static MonitorRuleRow From(VariableMonitorRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        PortName = rule.PortName ?? string.Empty,
        Pattern = rule.Pattern,
        IsEnabled = rule.IsEnabled,
        Order = rule.Order,
    };
}

public partial class MonitorValueRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SampledAt { get; set; } = string.Empty;

    [ObservableProperty]
    public partial long MatchCount { get; set; }
}
