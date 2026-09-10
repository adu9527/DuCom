using DuCom.Plugin;

namespace DuCom.Plugins.BackgroundImage;

public sealed partial class Plugin
{
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

        // Only the slideshow timer starts here; every image application goes through the
        // serialized EnqueueApplyAsync gate so concurrent edits cannot interleave.
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

            QueueApply(repaintOnly: false);
        });
        return true;
    }
}
