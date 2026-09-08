using System.IO;
using System.ComponentModel;
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
            BitmapSource? decoded = DecodeBounded(path);
            Set(apply.PluginId, generation, decoded, opacity);
        });
    }

    public void Clear(string? pluginId = null)
    {
        lock (_gate)
        {
            if (pluginId is not null && !string.Equals(pluginId, _ownerPluginId, StringComparison.Ordinal)) return;
            _ownerPluginId = null;
            _generation++;
        }
        Set(null, 0, null, null);
    }

    private static BitmapSource? DecodeBounded(string path)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || info.Length > MaximumImageFileBytes)
            {
                return null;
            }

            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = MaximumDecodePixelWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Set(string? pluginId, long generation, BitmapSource? source, double? opacity)
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
                if (pluginId is not null && (generation != _generation || !string.Equals(pluginId, _ownerPluginId, StringComparison.Ordinal))) return;
                _imageSource = source;
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
