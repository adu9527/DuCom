using System.Text.Json;
using DuCom.Plugin;

namespace DuCom.Plugins.BackgroundImage;

public sealed partial class Plugin
{
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

        // The command returns as soon as the new configuration is durable. A newer request
        // cancels any old image resolution/apply before entering the single serialized gate.
        QueueApply(repaintOnly: !structural);
        // Refresh the dispatcher's cached page after every configuration change (enable state,
        // opacity), not only when the displayed image changes.
        await PushSettingsPageAsync(cancellationToken);
    }

    private void QueueApply(bool repaintOnly)
    {
        CancellationToken token;
        lock (_gate)
        {
            _applyCancellation?.Cancel();
            _applyCancellation = new CancellationTokenSource();
            token = _applyCancellation.Token;
        }

        _ = RunQueuedApplyAsync(repaintOnly, token);
    }

    private async Task RunQueuedApplyAsync(bool repaintOnly, CancellationToken cancellationToken)
    {
        try
        {
            await EnqueueApplyAsync(cancellationToken, repaintOnly);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Api.Diagnostics.Warning($"Background apply failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Serializes every background application through one gate. Each run snapshots the latest
    /// configuration, so a slow stale run (old image, old enable state) can never overwrite a
    /// newer transition: the newest configuration always wins.
    /// </summary>
    private async Task EnqueueApplyAsync(CancellationToken cancellationToken, bool repaintOnly = false)
    {
        await _applyGate.WaitAsync(cancellationToken);
        try
        {
            await ApplyCurrentAsync(cancellationToken, repaintOnly);
        }
        finally
        {
            _applyGate.Release();
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
}
