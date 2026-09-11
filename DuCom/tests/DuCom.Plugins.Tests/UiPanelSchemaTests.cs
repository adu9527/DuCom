using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class UiPanelSchemaTests
{
    [Fact]
    public void CardPanelRoundTripsAndValidates()
    {
        IReadOnlyList<UiNode> nodes =
        [
            new UiPanelNode
            {
                Id = "settings",
                Presentation = UiPanelPresentation.Card,
                Title = "Settings",
                Direction = UiDirection.Horizontal,
                Width = 620,
                ItemWidth = 300,
                Wrap = false,
                Compact = true,
                VerticalCenter = true,
                Children = [new UiLabelNode { Text = "Value", FontSizeDelta = -2, MaxWidth = 240, VerticalCenter = true }],
            },
        ];

        string json = JsonSerializer.Serialize(nodes, DtoJson.Options);
        IReadOnlyList<UiNode> restored = JsonSerializer.Deserialize<IReadOnlyList<UiNode>>(json, DtoJson.Options)!;

        UiPanelNode panel = Assert.IsType<UiPanelNode>(Assert.Single(restored));
        Assert.Equal(UiPanelPresentation.Card, panel.Presentation);
        Assert.Equal("Settings", panel.Title);
        Assert.Equal(UiDirection.Horizontal, panel.Direction);
        Assert.Equal(620, panel.Width);
        Assert.Equal(300, panel.ItemWidth);
        Assert.False(panel.Wrap);
        Assert.True(panel.Compact);
        Assert.True(panel.VerticalCenter);
        UiLabelNode label = Assert.IsType<UiLabelNode>(Assert.Single(panel.Children));
        Assert.Equal(-2, label.FontSizeDelta);
        Assert.Equal(240, label.MaxWidth);
        Assert.True(label.VerticalCenter);
        Assert.True(UiContributionValidator.Validate(restored, out string? error), error);
    }

    [Fact]
    public void CardTitleUsesTextLimit()
    {
        IReadOnlyList<UiNode> nodes = [new UiPanelNode { Presentation = UiPanelPresentation.Card, Title = new string('x', UiSchemaLimits.MaximumTextLength + 1) }];
        Assert.False(UiContributionValidator.Validate(nodes, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ExpanderRoundTripsAndValidatesNestedForm()
    {
        IReadOnlyList<UiNode> nodes =
        [
            new UiExpanderNode
            {
                Id = "advanced",
                Title = "Advanced",
                Children = [new UiTextNode { FieldId = "name", Text = "value" }],
            },
        ];

        string json = JsonSerializer.Serialize(nodes, DtoJson.Options);
        UiExpanderNode restored = Assert.IsType<UiExpanderNode>(Assert.Single(JsonSerializer.Deserialize<IReadOnlyList<UiNode>>(json, DtoJson.Options)!));
        Assert.False(restored.IsExpanded);
        Assert.Equal("name", Assert.IsType<UiTextNode>(Assert.Single(restored.Children)).FieldId);
        Assert.True(UiContributionValidator.Validate(nodes, out string? error), error);
    }

    [Fact]
    public void ExpanderRequiresTitle()
    {
        Assert.False(UiContributionValidator.Validate([new UiExpanderNode()], out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void CompactControlsAndCheckboxChangeCommandRoundTrip()
    {
        IReadOnlyList<UiNode> nodes =
        [
            new UiCheckBoxNode { FieldId = "enabled", Label = "Enabled", CommandId = "form-changed", SubmitOnChange = true, IsEnabled = false },
            new UiTextNode { FieldId = "name", Width = 180, IsEnabled = false },
            new UiButtonNode { CommandId = "start", Text = "Start", IsEnabled = false },
            new UiProgressNode { Width = 220, Height = 14 },
        ];

        string json = JsonSerializer.Serialize(nodes, DtoJson.Options);
        IReadOnlyList<UiNode> restored = JsonSerializer.Deserialize<IReadOnlyList<UiNode>>(json, DtoJson.Options)!;

        UiCheckBoxNode checkbox = Assert.IsType<UiCheckBoxNode>(restored[0]);
        Assert.Equal("form-changed", checkbox.CommandId);
        Assert.True(checkbox.SubmitOnChange);
        Assert.False(checkbox.IsEnabled);
        Assert.Equal(180, Assert.IsType<UiTextNode>(restored[1]).Width);
        Assert.False(Assert.IsType<UiButtonNode>(restored[2]).IsEnabled);
        Assert.Equal(220, Assert.IsType<UiProgressNode>(restored[3]).Width);
        Assert.Equal(14, Assert.IsType<UiProgressNode>(restored[3]).Height);
        Assert.True(UiContributionValidator.Validate(restored, out string? error), error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(double.NaN)]
    public void InvalidLayoutWidthsAreRejected(double width)
    {
        Assert.False(UiContributionValidator.Validate([new UiPanelNode { Width = width }], out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void SubmitOnChangeRequiresCommand()
    {
        Assert.False(UiContributionValidator.Validate([new UiCheckBoxNode { FieldId = "enabled", SubmitOnChange = true }], out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ToolPageDimensionsRoundTripAndValidate()
    {
        PluginActivation activation = new()
        {
            ToolPages = [new ToolPageContribution { ContributionId = "wide", Title = "Wide", PreferredWidth = 1100, PreferredHeight = 700, MinWidth = 900 }],
        };
        string json = JsonSerializer.Serialize(activation, DtoJson.Options);
        ToolPageContribution page = JsonSerializer.Deserialize<PluginActivation>(json, DtoJson.Options)!.ToolPages.Single();
        Assert.Equal(1100, page.PreferredWidth);
        Assert.Equal(700, page.PreferredHeight);
        Assert.Equal(900, page.MinWidth);
        Assert.True(UiContributionValidator.Validate(activation, out string? error), error);
    }

    [Theory]
    [InlineData(479)]
    [InlineData(1601)]
    public void ToolPageWidthOutsideGenericLimitIsRejected(double width)
    {
        PluginActivation activation = new() { ToolPages = [new ToolPageContribution { ContributionId = "page", PreferredWidth = width }] };
        Assert.False(UiContributionValidator.Validate(activation, out string? error));
        Assert.NotNull(error);
    }
}
