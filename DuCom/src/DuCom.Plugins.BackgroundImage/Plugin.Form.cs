using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.BackgroundImage;

public sealed partial class Plugin
{
    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        switch (commandId)
        {
            case "open":
                await Api.Ui.UpdateToolPageAsync("settings", BuildSettingsNodes(), cancellationToken);
                return CommandInvokeOutcome.Complete();
            case "pick-image":
                FilesPickResult? image = await Api.Files.PickReadAsync(FilePickReadOptions.File("png", "jpg", "jpeg", "bmp", "webp") with { FilterName = Zh("图片", "Images"), Remember = true }, cancellationToken);
                if (image is not null)
                {
                    await ApplyConfigurationAsync(_config.Enabled, "single", image.DisplayPath, _config.FolderPath, _config.IntervalSeconds, _config.Opacity, cancellationToken);
                    await PushSettingsPageAsync(cancellationToken);
                }

                return CommandInvokeOutcome.Complete();
            case "pick-folder":
                FilesPickResult? folder = await Api.Files.PickReadAsync(FilePickReadOptions.Directory() with { Remember = true }, cancellationToken);
                if (folder is not null)
                {
                    await ApplyConfigurationAsync(_config.Enabled, "sequential", _config.ImagePath, folder.DisplayPath, _config.IntervalSeconds, _config.Opacity, cancellationToken);
                    await PushSettingsPageAsync(cancellationToken);
                }

                return CommandInvokeOutcome.Complete();
            case "apply":
                // Live apply (checkbox/slider/combo edits): never republish the page here, the
                // calling control already shows the new value and a rebuild would interrupt drags.
                await OnSettingsApplyAsync(formValues, cancellationToken);
                return CommandInvokeOutcome.Complete();
            case "reset-defaults":
                // Restore factory defaults but keep the user's picked image/folder paths.
                await ApplyConfigurationAsync(true, "single", _config.ImagePath, _config.FolderPath, 300, 0.18d, cancellationToken);
                await PushSettingsPageAsync(cancellationToken);
                return CommandInvokeOutcome.Complete(Zh("已恢复默认参数", "Defaults restored"));
            case "next":
                return AdvanceNow() ? CommandInvokeOutcome.Complete() : CommandInvokeOutcome.Complete(Zh("没有可用图片", "No images available"));
            default:
                return CommandInvokeOutcome.Reject($"Unknown command '{commandId}'.");
        }
    }

    private async Task PushSettingsPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Api.Ui.UpdateToolPageAsync("settings", BuildSettingsNodes(), cancellationToken);
        }
        catch (PluginHostException exception)
        {
            // Silent UI staleness caused a long-living "settings memory" bug: always surface it.
            Api.Diagnostics.Warning($"Settings page push failed: {exception.Code} {exception.Message}");
        }
    }

    private IReadOnlyList<UiNode> BuildSettingsNodes()
    {
        string currentImage;
        lock (_gate)
        {
            currentImage = _currentImagePath
                ?? (string.IsNullOrEmpty(_config.ImagePath) ? string.Empty : _config.ImagePath);
        }

        return
        [
            new UiLabelNode { Text = Zh("背景图", "Background image"), Style = UiTextStyle.Heading },
            new UiCheckBoxNode { FieldId = "enabled", Label = Zh("启用背景图", "Enable background"), IsChecked = _config.Enabled },
            new UiLabelNode { Text = string.IsNullOrEmpty(currentImage) ? Zh("未选择图片", "No image selected") : currentImage, Wrap = true },
            new UiLabelNode { Text = string.IsNullOrEmpty(_config.FolderPath) ? Zh("未选择轮播目录", "No slideshow folder selected") : _config.FolderPath, Wrap = true },
            new UiPanelNode { Direction = UiDirection.Horizontal, Children = [new UiButtonNode { CommandId = "pick-image", Text = Zh("选择图片…", "Choose image...") }, new UiButtonNode { CommandId = "pick-folder", Text = Zh("选择目录…", "Choose folder...") }, new UiButtonNode { CommandId = "next", Text = Zh("下一张", "Next") }] },
            new UiSelectNode { FieldId = "playback", Selected = _config.Playback, Options = [new UiSelectOption { Value = "single", Label = Zh("单张", "Single image") }, new UiSelectOption { Value = "sequential", Label = Zh("顺序轮播", "Sequential") }, new UiSelectOption { Value = "random", Label = Zh("随机轮播", "Random") }] },
            new UiSliderNode { FieldId = "opacity", Min = 0, Max = 100, Step = 1, Value = Math.Round(_config.Opacity * 100), UnitLabel = "%" },
            new UiTextNode { FieldId = "intervalSeconds", Text = _config.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), Placeholder = Zh("轮播间隔（秒）", "Slideshow interval (seconds)") },
            new UiButtonNode { CommandId = "reset-defaults", Text = Zh("恢复默认参数", "Restore defaults") },
        ];
    }

    private string Zh(string chinese, string english) => Api.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
}
