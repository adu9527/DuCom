using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.BackgroundImage;

public sealed class Plugin : DuComPlugin
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    private readonly Lock _gate = new();
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

    public override async Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
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
        return activation;
    }

    public override async Task<SettingsApplyOutcome> OnSettingsApplyAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        bool enabled = _config.Enabled;
        string playback = _config.Playback;
        string imagePath = _config.ImagePath;
        string folderPath = _config.FolderPath;
        int interval = _config.IntervalSeconds;
        double opacity = _config.Opacity;
        lock (_gate)
        {
            if (values.TryGetValue("enabled", out string? enabledValue))
            {
                enabled = bool.TryParse(enabledValue, out bool parsed) && parsed;
            }

            if (values.TryGetValue("imagePath", out string? pathValue))
            {
                imagePath = pathValue ?? string.Empty;
                playback = playback != "sequential" && playback != "random" ? "single" : playback;
            }

            if (values.TryGetValue("folderPath", out string? folderValue))
            {
                folderPath = folderValue ?? string.Empty;
            }

            if (values.TryGetValue("playback", out string? playbackValue) && playbackValue is "single" or "sequential" or "random")
            {
                playback = playbackValue;
            }

            if (values.TryGetValue("intervalSeconds", out string? intervalValue) && int.TryParse(intervalValue, out int seconds))
            {
                interval = Math.Clamp(seconds, 1, 86_400);
            }

            if (values.TryGetValue("opacity", out string? opacityValue) && double.TryParse(opacityValue, System.Globalization.CultureInfo.InvariantCulture, out double parsedOpacity))
            {
                opacity = NormalizeOpacity(parsedOpacity);
            }
        }

        await ApplyConfigurationAsync(enabled, playback, imagePath, folderPath, interval, opacity, cancellationToken);
        return new SettingsApplyOutcome(true);
    }

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
            case "next":
                return AdvanceNow() ? CommandInvokeOutcome.Complete() : CommandInvokeOutcome.Complete(Zh("没有可用图片", "No images available"));
            default:
                return CommandInvokeOutcome.Reject($"Unknown command '{commandId}'.");
        }
    }

    public override Task DeactivateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _carouselCancellation?.Cancel();
            _carouselCancellation = null;
        }

        _ = Api.Background.ClearAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    private static double NormalizeOpacity(double value) => value > 1d ? Math.Clamp(value / 100d, 0d, 1d) : Math.Clamp(value, 0d, 1d);

    private async Task ApplyConfigurationAsync(bool enabled, string playback, string imagePath, string folderPath, int intervalSeconds, double opacity, CancellationToken cancellationToken)
    {
        bool structural;
        lock (_gate)
        {
            structural = enabled != _config.Enabled
                || playback != _config.Playback
                || !string.Equals(imagePath, _config.ImagePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(folderPath, _config.FolderPath, StringComparison.OrdinalIgnoreCase)
                || intervalSeconds != _config.IntervalSeconds;
            _config = _config with { Enabled = enabled, Playback = playback, ImagePath = imagePath, FolderPath = folderPath, IntervalSeconds = intervalSeconds, Opacity = opacity };
            if (structural)
            {
                _playlist = [];
                _playlistIndex = -1;
            }
        }

        string json = JsonSerializer.Serialize(_config, BackgroundConfig.JsonOptions);
        await Api.Storage.WriteAsync(json, cancellationToken);
        if (structural)
        {
            RestartCarousel();
        }
        else
        {
            // Opacity-only change: fast path that reuses the cached image token.
            await ApplyCurrentAsync(cancellationToken, repaintOnly: true);
        }
    }

    private async Task ApplyCurrentAsync(CancellationToken cancellationToken, bool repaintOnly = false)
    {
        bool enabled;
        double opacity;
        lock (_gate)
        {
            enabled = _config.Enabled;
            opacity = _config.Opacity;
        }

        if (!enabled)
        {
            await SafeClearAsync();
            await RecordCurrentImageAsync(null, null);
            return;
        }

        string? path = repaintOnly ? _currentImagePath : await ResolveCurrentImageAsync(cancellationToken);
        if (path is null)
        {
            await SafeClearAsync();
            await RecordCurrentImageAsync(null, null);
            return;
        }

        try
        {
            string? token = _currentImageToken;
            if (!repaintOnly || token is null || !string.Equals(path, _currentImagePath, StringComparison.OrdinalIgnoreCase))
            {
                token = await Api.Files.RequestRememberedReadTokenAsync(path, cancellationToken);
                if (token is null)
                {
                    await SafeClearAsync();
                    await RecordCurrentImageAsync(null, null);
                    return;
                }
            }

            await Api.Background.SetAsync(token, opacity, cancellationToken);
            await RecordCurrentImageAsync(path, token);
        }
        catch (PluginHostException exception)
        {
            Api.Diagnostics.Warning($"Background apply failed: {exception.Code} {exception.Message}");
            await SafeClearAsync();
            await RecordCurrentImageAsync(null, null);
        }
    }

    private async Task RecordCurrentImageAsync(string? path, string? token)
    {
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(path, _currentImagePath, StringComparison.OrdinalIgnoreCase);
            _currentImagePath = path;
            _currentImageToken = token;
        }

        if (changed)
        {
            await PushSettingsPageAsync(CancellationToken.None);
        }
    }

    private async Task PushSettingsPageAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Api.Ui.UpdateToolPageAsync("settings", BuildSettingsNodes(), cancellationToken);
        }
        catch (PluginHostException)
        {
        }
    }

    private async Task<string?> ResolveCurrentImageAsync(CancellationToken cancellationToken)
    {
        string playback, imagePath;
        lock (_gate)
        {
            playback = _config.Playback;
            imagePath = _config.ImagePath;
        }

        if (playback == "single")
        {
            return string.IsNullOrWhiteSpace(imagePath) ? null : imagePath;
        }

        List<string> playlist = await GetPlaylistAsync(cancellationToken);
        if (playlist.Count == 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_playlistIndex < 0 || _playlistIndex >= playlist.Count)
            {
                _playlistIndex = playback == "random" ? Random.Shared.Next(playlist.Count) : 0;
            }

            return playlist[_playlistIndex];
        }
    }

    private async Task<List<string>> GetPlaylistAsync(CancellationToken cancellationToken)
    {
        string folderPath;
        lock (_gate)
        {
            folderPath = _config.FolderPath;
            if (_playlist.Count > 0 || string.IsNullOrWhiteSpace(folderPath))
            {
                return _playlist;
            }
        }

        try
        {
            string? folderToken = await Api.Files.RequestRememberedReadTokenAsync(folderPath, cancellationToken);
            if (folderToken is null)
            {
                return [];
            }

            IReadOnlyList<DuCom.Plugin.Dto.PickedEntry> entries = await Api.Files.ListAsync(folderToken, cancellationToken);
            List<string> images = [.. entries
                .Where(entry => !entry.IsDirectory && ImageExtensions.Contains(Path.GetExtension(entry.Name).ToLowerInvariant()))
                .Select(entry => Path.Combine(folderPath, entry.Name))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
            lock (_gate)
            {
                _playlist = images;
                _playlistIndex = -1;
            }

            return images;
        }
        catch (PluginHostException exception)
        {
            Api.Diagnostics.Warning($"Cannot list the background folder: {exception.Code}");
            return [];
        }
    }

    private void RestartCarousel()
    {
        CancellationTokenSource cancellation;
        bool startCarousel;
        lock (_gate)
        {
            _carouselCancellation?.Cancel();
            cancellation = new CancellationTokenSource();
            _carouselCancellation = cancellation;
            _playlist = [];
            _playlistIndex = -1;
            startCarousel = _config.Enabled && _config.Playback != "single";
        }

        // A single image has no timer, but still must be shown immediately.
        _ = Task.Run(() => ApplyCurrentAsync(cancellation.Token));
        if (!startCarousel) return;

        new Thread(() => CarouselLoop(cancellation.Token))
        {
            IsBackground = true,
            Name = "background-carousel",
        }.Start();
    }

    private void CarouselLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int interval;
                lock (_gate)
                {
                    interval = _config.IntervalSeconds;
                }

                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(interval)))
                {
                    return;
                }

                lock (_gate)
                {
                    _playlistIndex = -1;
                }

                AdvanceNow();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool AdvanceNow()
    {
        _ = Task.Run(async () =>
        {
            List<string> playlist = await GetPlaylistAsync(CancellationToken.None);
            if (playlist.Count == 0)
            {
                return;
            }

            string playback;
            lock (_gate)
            {
                playback = _config.Playback;
                if (playback == "random" && playlist.Count > 1)
                {
                    int next;
                    do
                    {
                        next = Random.Shared.Next(playlist.Count);
                    }
                    while (next == _playlistIndex);
                    _playlistIndex = next;
                }
                else
                {
                    _playlistIndex = (_playlistIndex + 1) % playlist.Count;
                }
            }

            await ApplyCurrentAsync(CancellationToken.None);
        });
        return true;
    }

    private async Task SafeClearAsync()
    {
        try
        {
            await Api.Background.ClearAsync(CancellationToken.None);
        }
        catch (PluginHostException)
        {
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
        ];
    }

    private string Zh(string chinese, string english) => Api.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
}
