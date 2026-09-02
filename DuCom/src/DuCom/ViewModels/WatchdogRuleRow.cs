using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Diagnostics;

namespace DuCom.ViewModels;

/// <summary>Editable row for the watchdog rules page.</summary>
public partial class WatchdogRuleRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Pattern { get; set; } = string.Empty;

    [ObservableProperty]
    public partial WatchdogMatchMode Mode { get; set; } = WatchdogMatchMode.Contains;

    [ObservableProperty]
    public partial bool IsCaseSensitive { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int ExpectWithinSeconds { get; set; } = 30;

    [ObservableProperty]
    public partial int ThrottleSeconds { get; set; } = 60;

    [ObservableProperty]
    public partial WatchdogActionKind ActionKind { get; set; } = WatchdogActionKind.Hint;

    [ObservableProperty]
    public partial string ActionCommand { get; set; } = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();

    public WatchdogRule ToRule() => new(
        Id,
        Name,
        Pattern,
        Mode,
        IsCaseSensitive,
        IsEnabled,
        Math.Max(1, ExpectWithinSeconds),
        Math.Max(1, ThrottleSeconds),
        ActionKind,
        ActionCommand);

    public static WatchdogRuleRow From(WatchdogRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        Pattern = rule.Pattern,
        Mode = rule.Mode,
        IsCaseSensitive = rule.IsCaseSensitive,
        IsEnabled = rule.IsEnabled,
        ExpectWithinSeconds = rule.ExpectWithinSeconds,
        ThrottleSeconds = rule.ThrottleSeconds,
        ActionKind = rule.ActionKind,
        ActionCommand = rule.ActionCommand,
    };
}
