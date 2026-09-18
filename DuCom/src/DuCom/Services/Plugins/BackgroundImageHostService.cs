using System.IO;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DuCom.Services.Plugins;

/// <summary>
/// Host-owned background rendering for the background-image capability. Decodes on a
/// background thread with hard caps (file size and pixel width) and applies the frozen
/// bitmap plus opacity to the main-window overlay on the UI thread. The winning plugin is
/// the last one that applied; deactivation clears.
/// </summary>
public sealed class BackgroundImageHostService : INotifyPropertyChanged
{
    private const long MaximumImageFileBytes = 20L * 1024 * 1024;
    private const int MaximumDecodePixelWidth = 1920;

    private readonly object _gate = new();
    private ImageSource? _imageSource;
    private byte[]? _imageDigest;
    private double _opacity;
    private bool _enabled;
    private string? _ownerPluginId;
    private long _generation;

    public event EventHandler? Changed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImageSource? ImageSource
    {
        get
        {
            lock (_gate)
            {
                return _imageSource;
            }
        }
    }

    public double Opacity
    {
        get
        {
            lock (_gate)
            {
                return _opacity;
            }
        }
    }

    public bool Enabled
    {
        get
        {
            lock (_gate)
            {
                return _enabled;
            }
        }
    }

    public void Apply(PluginHost.BackgroundApply apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (apply.ImageTokenPath is null)
        {
            Clear(apply.PluginId);
            return;
        }

        string path = apply.ImageTokenPath;
        double? opacity = apply.Opacity;
        long generation;
        lock (_gate)
        {
            _ownerPluginId = apply.PluginId;
            generation = ++_generation;
        }
        _ = Task.Run(() =>
        {
            BitmapSource? decoded = DecodeBounded(path, generation, out byte[]? digest);
            Set(apply.PluginId, generation, decoded, opacity, digest);
        });
    }

    public void Clear(string? pluginId = null)
    {
        long generation;
        lock (_gate)
        {
            if (pluginId is not null && !string.Equals(pluginId, _ownerPluginId, StringComparison.Ordinal)) return;
            _ownerPluginId = null;
            generation = ++_generation;
            _imageDigest = null;
        }
        Set(null, generation, null, null, null);
    }

    private BitmapSource? DecodeBounded(string path, long generation, out byte[]? digest)
    {
        digest = null;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return null;
            // Hash and decode the same handle, denying writes/deletion while it is open.
            // Metadata alone cannot detect same-path edits with preserved timestamps.
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumImageFileBytes)
            {
                return null;
            }

            digest = SHA256.HashData(stream);
            lock (_gate)
            {
                if (generation != _generation) return null;
                if (_imageDigest is not null && digest.AsSpan().SequenceEqual(_imageDigest))
                {
                    return _imageSource as BitmapSource;
                }
            }

            stream.Position = 0;
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = MaximumDecodePixelWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Set(string? pluginId, long generation, BitmapSource? source, double? opacity, byte[]? digest)
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        application.Dispatcher.BeginInvoke(() =>
        {
            lock (_gate)
            {
                if (generation != _generation || !string.Equals(pluginId, _ownerPluginId, StringComparison.Ordinal)) return;
                _imageSource = source;
                _imageDigest = source is null ? null : digest;
                if (opacity.HasValue)
                {
                    _opacity = Math.Clamp(opacity.Value, 0d, 1d);
                }

                _enabled = source is not null;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Opacity)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }
}
