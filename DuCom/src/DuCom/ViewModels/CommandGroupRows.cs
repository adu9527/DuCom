using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DuCom.Core.Sending;

namespace DuCom.ViewModels;

public sealed partial class CommandGroupRow(Guid groupId, string name, int commandCount) : ObservableObject
{
    public Guid GroupId { get; } = groupId;

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    public partial int CommandCount { get; set; } = commandCount;

    [ObservableProperty]
    public partial bool IsRunning { get; internal set; }
}

public sealed partial class ScriptCommandRow : ObservableObject
{
    private Guid _id;
    private readonly Dictionary<string, string> _targetStates = new(StringComparer.OrdinalIgnoreCase);

    public Guid Id => _id;

    public static ScriptCommandRow From(ScriptCommand command) => new()
    {
        _id = command.Id,
        NameText = command.Name,
        OrderValue = command.Order,
        PayloadText = command.Payload,
        IsHexEnabled = command.IsHex,
        DelayMsValue = command.DelayMilliseconds,
        HasResultCheck = command.IsResultCheck,
        ExpectedResultText = command.ExpectedResult,
        ResultTimeoutMsValue = command.ResultTimeoutMilliseconds,
        SelectedNewline = command.Newline,
    };

    [ObservableProperty]
    public partial string NameText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int OrderValue { get; set; }

    [ObservableProperty]
    public partial string PayloadText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsHexEnabled { get; set; }

    [ObservableProperty]
    public partial int DelayMsValue { get; set; }

    [ObservableProperty]
    public partial bool HasResultCheck { get; set; }

    [ObservableProperty]
    public partial string ExpectedResultText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ResultTimeoutMsValue { get; set; }

    [ObservableProperty]
    public partial NewlinePolicy SelectedNewline { get; set; }

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    public string NameDisplay => string.IsNullOrWhiteSpace(NameText) ? "—" : NameText;

    public string PayloadDisplay => string.IsNullOrWhiteSpace(PayloadText) ? "—" : PayloadText;

    public bool HasNewline => SelectedNewline != NewlinePolicy.None;

    public string NewlineLabel => SelectedNewline switch
    {
        NewlinePolicy.Cr => "CR",
        NewlinePolicy.Lf => "LF",
        NewlinePolicy.CrLf => "CRLF",
        _ => string.Empty,
    };

    partial void OnNameTextChanged(string value) => OnPropertyChanged(nameof(NameDisplay));

    partial void OnPayloadTextChanged(string value) => OnPropertyChanged(nameof(PayloadDisplay));

    partial void OnSelectedNewlineChanged(NewlinePolicy value)
    {
        OnPropertyChanged(nameof(NewlineLabel));
        OnPropertyChanged(nameof(HasNewline));
    }

    public void SetState(string? targetName, string state)
    {
        if (string.IsNullOrWhiteSpace(targetName))
        {
            StateText = state;
            return;
        }

        _targetStates[targetName] = state;
        StateText = string.Join("; ", _targetStates
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}: {pair.Value}"));
    }

    public ScriptCommand ToCommand(int fallbackOrder) => new(
        _id == Guid.Empty ? Guid.NewGuid() : _id,
        NameText,
        OrderValue >= 0 ? OrderValue : fallbackOrder,
        PayloadText,
        IsHexEnabled,
        Math.Max(DelayMsValue, 0),
        HasResultCheck,
        ExpectedResultText,
        Math.Clamp(ResultTimeoutMsValue <= 0 ? 5_000 : ResultTimeoutMsValue, 1, 3_600_000),
        SelectedNewline);
}

public sealed partial class CommandTargetPortRow : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _updating;

    public CommandTargetPortRow(string portName, Action selectionChanged)
    {
        PortName = portName;
        _selectionChanged = selectionChanged;
    }

    public string PortName { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    public string StateText => GetResourceString(IsOpen ? "Commands.TargetOpen" : "Commands.TargetClosed");

    private static string GetResourceString(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_updating)
        {
            _selectionChanged();
        }
    }

    public void Update(bool isSelected, bool isOpen)
    {
        _updating = true;
        try
        {
            IsSelected = isSelected;
            IsOpen = isOpen;
            OnPropertyChanged(nameof(StateText));
        }
        finally
        {
            _updating = false;
        }
    }
}
