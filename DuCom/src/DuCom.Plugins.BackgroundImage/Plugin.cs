using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.BackgroundImage;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gate lives exactly as long as the plugin instance, which lives exactly as long as the worker process; the host deactivates plugins by tearing down the process.")]
public sealed partial class Plugin : DuComPlugin
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private CancellationTokenSource? _applyCancellation;
    private BackgroundConfig _config = new();
    private CancellationTokenSource? _carouselCancellation;
    private List<string> _playlist = [];
    private int _playlistIndex = -1;
    private string? _currentImagePath;
    private string? _currentImageToken;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        string? json = await Api.Storage.ReadAsync(cancellationToken);
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                BackgroundConfig? loaded = JsonSerializer.Deserialize<BackgroundConfig>(json, BackgroundConfig.JsonOptions);
                if (loaded is not null)
                {
                    _config = loaded;
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        PluginActivation activation = new()
        {
            SettingsPanels =
            [
                new SettingsPanelContribution
                {
                    ContributionId = "background",
                    Title = Zh("背景图片", "Background image"),
                    Order = 100,
                    Fields =
                    [
                        new SettingsField { Id = "enabled", Type = SettingsFieldType.Bool, Label = Zh("启用", "Enabled"), DefaultValue = "true" },
                        new SettingsField { Id = "imagePath", Type = SettingsFieldType.Path, Label = Zh("单张图片", "Single image"), Placeholder = Zh("选择一张图片", "Pick an image") },
                        new SettingsField { Id = "folderPath", Type = SettingsFieldType.FolderPath, Label = Zh("图片目录", "Image folder"), Placeholder = Zh("选择轮播目录", "Pick a folder") },
                        new SettingsField
                        {
                            Id = "playback",
                            Type = SettingsFieldType.Enum,
                            Label = Zh("播放模式", "Playback"),
                            DefaultValue = "single",
                            Options =
                            [
                                new UiSelectOption { Value = "single", Label = Zh("单张", "Single image") },
                                new UiSelectOption { Value = "sequential", Label = Zh("顺序轮播", "Sequential") },
                                new UiSelectOption { Value = "random", Label = Zh("随机轮播", "Random") },
                            ],
                        },
                        new SettingsField { Id = "intervalSeconds", Type = SettingsFieldType.Int, Label = Zh("轮播间隔（秒）", "Interval (s)"), Min = 1, Max = 86400, DefaultValue = "300", Unit = "s" },
                        new SettingsField { Id = "opacity", Type = SettingsFieldType.Number, Label = Zh("不透明度", "Opacity"), Min = 0, Max = 1, Step = 0.01, DefaultValue = "0.18" },
                    ],
                },
            ],
            BackgroundImages = [new BackgroundImageContribution { ContributionId = "main", Order = 100 }],
            Menus = [new MenuContribution { ContributionId = "open-settings", Label = Zh("背景图", "Background image"), CommandId = "open", PageId = "settings", Order = 90 }],
            ToolPages = [new ToolPageContribution { ContributionId = "settings", Title = Zh("背景图", "Background image"), Order = 90, Nodes = BuildSettingsNodes() }],
        };

        RestartCarousel();
        QueueApply(repaintOnly: false);
        return Task.FromResult(activation);
    }

    public override Task DeactivateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _carouselCancellation?.Cancel();
            _carouselCancellation = null;
            _applyCancellation?.Cancel();
            _applyCancellation = null;
        }

        _ = Api.Background.ClearAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

}
