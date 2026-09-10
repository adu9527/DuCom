using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Parsing;

namespace DuCom.ViewModels;

public partial class HighlightFilterRulesViewModel
{
    private bool _isLoadingSelection;

    public ObservableCollection<RuleEditor> Rules { get; } = [];

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
}
