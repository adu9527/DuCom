using System.Text.Json.Serialization;

namespace DuCom.Plugin;

public enum UiDirection
{
    Vertical,
    Horizontal,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$t")]
[JsonDerivedType(typeof(UiPanelNode), "panel")]
[JsonDerivedType(typeof(UiLabelNode), "label")]
[JsonDerivedType(typeof(UiButtonNode), "button")]
[JsonDerivedType(typeof(UiTextNode), "textbox")]
[JsonDerivedType(typeof(UiCheckBoxNode), "checkbox")]
[JsonDerivedType(typeof(UiSelectNode), "select")]
[JsonDerivedType(typeof(UiSliderNode), "slider")]
[JsonDerivedType(typeof(UiListNode), "list")]
[JsonDerivedType(typeof(UiImageNode), "image")]
[JsonDerivedType(typeof(UiProgressNode), "progress")]
[JsonDerivedType(typeof(UiDividerNode), "divider")]
public abstract record UiNode
{
    [JsonPropertyName("id")] public string? Id { get; init; }
}

public sealed record UiPanelNode : UiNode
{
    [JsonPropertyName("direction")] public UiDirection Direction { get; init; } = UiDirection.Vertical;
    [JsonPropertyName("children")] public IReadOnlyList<UiNode> Children { get; init; } = [];
}

public enum UiTextStyle
{
    Normal,
    Heading,
    Caption,
    Accent,
    Warning,
}

public sealed record UiLabelNode : UiNode
{
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    [JsonPropertyName("style")] public UiTextStyle Style { get; init; } = UiTextStyle.Normal;
    [JsonPropertyName("wrap")] public bool Wrap { get; init; }
}

public sealed record UiButtonNode : UiNode
{
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    [JsonPropertyName("commandId")] public string CommandId { get; init; } = string.Empty;
    [JsonPropertyName("accent")] public bool Accent { get; init; }
    [JsonPropertyName("submitForm")] public bool SubmitForm { get; init; }
}

public sealed record UiTextNode : UiNode
{
    [JsonPropertyName("fieldId")] public string FieldId { get; init; } = string.Empty;
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    [JsonPropertyName("placeholder")] public string? Placeholder { get; init; }
    [JsonPropertyName("multiline")] public bool Multiline { get; init; }
    [JsonPropertyName("readOnly")] public bool ReadOnly { get; init; }
}

public sealed record UiCheckBoxNode : UiNode
{
    [JsonPropertyName("fieldId")] public string FieldId { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("checked")] public bool IsChecked { get; init; }
}

public sealed record UiSelectOption
{
    [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
}

public sealed record UiSelectNode : UiNode
{
    [JsonPropertyName("fieldId")] public string FieldId { get; init; } = string.Empty;
    [JsonPropertyName("options")] public IReadOnlyList<UiSelectOption> Options { get; init; } = [];
    [JsonPropertyName("selected")] public string? Selected { get; init; }
}

public sealed record UiSliderNode : UiNode
{
    [JsonPropertyName("fieldId")] public string FieldId { get; init; } = string.Empty;
    [JsonPropertyName("min")] public double Min { get; init; }
    [JsonPropertyName("max")] public double Max { get; init; } = 100;
    [JsonPropertyName("step")] public double Step { get; init; } = 1;
    [JsonPropertyName("value")] public double Value { get; init; }
    [JsonPropertyName("unitLabel")] public string? UnitLabel { get; init; }
}

public sealed record UiListItem
{
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    [JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed record UiListNode : UiNode
{
    [JsonPropertyName("items")] public IReadOnlyList<UiListItem> Items { get; init; } = [];
}

public sealed record UiImageNode : UiNode
{
    [JsonPropertyName("token")] public string Token { get; init; } = string.Empty;
    [JsonPropertyName("alt")] public string? Alt { get; init; }
    [JsonPropertyName("maxHeight")] public int MaxHeight { get; init; } = 320;
}

public sealed record UiProgressNode : UiNode
{
    [JsonPropertyName("percent")] public int? Percent { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
}

public sealed record UiDividerNode : UiNode;

public static class UiSchemaLimits
{
    public const int MaximumNodeCount = 400;
    public const int MaximumDepth = 10;
    public const int MaximumChildrenPerPanel = 64;
    public const int MaximumListItems = 500;
    public const int MaximumTextLength = 4000;
    public const int MaximumOptions = 64;
    public const long MaximumImageSourceBytes = 20 * 1024 * 1024;
    public const int MaximumImageDecodePixelWidth = 4096;
}

public sealed record MenuContribution
{
    [JsonPropertyName("contributionId")] public string ContributionId { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("commandId")] public string CommandId { get; init; } = string.Empty;
    [JsonPropertyName("pageId")] public string? PageId { get; init; }
    [JsonPropertyName("order")] public int Order { get; init; } = 100;
}

public enum SettingsFieldType
{
    Bool,
    Int,
    Number,
    String,
    Enum,
    Path,
    FolderPath,
}

public sealed record SettingsField
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("type")] public SettingsFieldType Type { get; init; } = SettingsFieldType.String;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("min")] public double? Min { get; init; }
    [JsonPropertyName("max")] public double? Max { get; init; }
    [JsonPropertyName("step")] public double? Step { get; init; }
    [JsonPropertyName("unit")] public string? Unit { get; init; }
    [JsonPropertyName("options")] public IReadOnlyList<UiSelectOption> Options { get; init; } = [];
    [JsonPropertyName("defaultValue")] public string DefaultValue { get; init; } = string.Empty;
    [JsonPropertyName("placeholder")] public string? Placeholder { get; init; }
}

public sealed record SettingsPanelContribution
{
    [JsonPropertyName("contributionId")] public string ContributionId { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("order")] public int Order { get; init; } = 100;
    [JsonPropertyName("fields")] public IReadOnlyList<SettingsField> Fields { get; init; } = [];
}

public sealed record ToolPageContribution
{
    [JsonPropertyName("contributionId")] public string ContributionId { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("order")] public int Order { get; init; } = 100;
    [JsonPropertyName("nodes")] public IReadOnlyList<UiNode> Nodes { get; init; } = [];
}

public sealed record BackgroundImageContribution
{
    [JsonPropertyName("contributionId")] public string ContributionId { get; init; } = string.Empty;
    [JsonPropertyName("order")] public int Order { get; init; } = 100;
}

public sealed record PluginActivation
{
    public IReadOnlyList<MenuContribution> Menus { get; init; } = [];
    public IReadOnlyList<SettingsPanelContribution> SettingsPanels { get; init; } = [];
    public IReadOnlyList<ToolPageContribution> ToolPages { get; init; } = [];
    public IReadOnlyList<BackgroundImageContribution> BackgroundImages { get; init; } = [];

    public static PluginActivation Empty { get; } = new();
}

public static class UiContributionValidator
{
    public static bool Validate(IReadOnlyList<UiNode> nodes, out string? error)
    {
        int count = 0;
        return ValidateNodes(nodes, 0, ref count, out error);
    }

    public static bool Validate(PluginActivation activation, out string? error)
    {
        error = null;
        foreach (MenuContribution menu in activation.Menus)
        {
            if (string.IsNullOrWhiteSpace(menu.ContributionId) || menu.ContributionId.Length > 64
                || string.IsNullOrWhiteSpace(menu.CommandId) || menu.CommandId.Length > 64
                || string.IsNullOrWhiteSpace(menu.Label) || menu.Label.Length > UiSchemaLimits.MaximumTextLength)
            {
                error = $"Menu contribution '{menu.ContributionId}' has invalid fields.";
                return false;
            }
        }

        foreach (SettingsPanelContribution panel in activation.SettingsPanels)
        {
            if (string.IsNullOrWhiteSpace(panel.ContributionId) || panel.ContributionId.Length > 64
                || panel.Fields.Count > 64)
            {
                error = $"Settings panel '{panel.ContributionId}' is invalid or too large.";
                return false;
            }

            foreach (SettingsField field in panel.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Id) || field.Id.Length > 64
                    || string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > UiSchemaLimits.MaximumTextLength
                    || field.Options.Count > UiSchemaLimits.MaximumOptions)
                {
                    error = $"Settings field '{field.Id}' is invalid.";
                    return false;
                }
            }
        }

        foreach (ToolPageContribution page in activation.ToolPages)
        {
            if (string.IsNullOrWhiteSpace(page.ContributionId) || page.ContributionId.Length > 64
                || !Validate(page.Nodes, out error))
            {
                error ??= $"Tool page '{page.ContributionId}' is invalid.";
                return false;
            }
        }

        foreach (BackgroundImageContribution background in activation.BackgroundImages)
        {
            if (string.IsNullOrWhiteSpace(background.ContributionId) || background.ContributionId.Length > 64)
            {
                error = "Background contribution id is invalid.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool ValidateNodes(IReadOnlyList<UiNode> nodes, int depth, ref int count, out string? error)
    {
        error = null;
        if (depth > UiSchemaLimits.MaximumDepth)
        {
            error = $"UI node depth exceeds {UiSchemaLimits.MaximumDepth}.";
            return false;
        }

        foreach (UiNode node in nodes)
        {
            if (++count > UiSchemaLimits.MaximumNodeCount)
            {
                error = $"UI node count exceeds {UiSchemaLimits.MaximumNodeCount}.";
                return false;
            }

            switch (node)
            {
                case UiPanelNode panel:
                    if (panel.Children.Count > UiSchemaLimits.MaximumChildrenPerPanel)
                    {
                        error = $"Panel '{panel.Id}' exceeds {UiSchemaLimits.MaximumChildrenPerPanel} children.";
                        return false;
                    }

                    if (!ValidateNodes(panel.Children, depth + 1, ref count, out error))
                    {
                        return false;
                    }

                    break;
                case UiLabelNode label when label.Text.Length > UiSchemaLimits.MaximumTextLength:
                case UiTextNode text when text.Text.Length > UiSchemaLimits.MaximumTextLength:
                case UiButtonNode button when button.Text.Length > UiSchemaLimits.MaximumTextLength:
                    error = "UI text exceeds the maximum length.";
                    return false;
                case UiListNode list when list.Items.Count > UiSchemaLimits.MaximumListItems:
                    error = $"List '{node.Id}' exceeds {UiSchemaLimits.MaximumListItems} items.";
                    return false;
                case UiSelectNode select when select.Options.Count > UiSchemaLimits.MaximumOptions:
                    error = $"Select '{node.Id}' exceeds {UiSchemaLimits.MaximumOptions} options.";
                    return false;
            }
        }

        return true;
    }
}

