using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class PluginManagerViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private bool _syncingFromMainViewModel;

    public PluginManagerViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        PluginDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuCom", "Plugins");
        BackgroundImageEnabled = mainViewModel.BackgroundImageEnabled;
        BackgroundImagePath = mainViewModel.BackgroundImagePath;
        BackgroundImageFolderPath = mainViewModel.BackgroundImageFolderPath;
        BackgroundImageIntervalSeconds = mainViewModel.BackgroundImageIntervalSeconds;
        BackgroundImageOpacity = mainViewModel.BackgroundImageOpacity;
        PlaybackModes =
        [
            new PlaybackModeOption(BackgroundImagePlaybackMode.SingleImage, Resource("Plugins.BackgroundImage.Mode.Single")),
            new PlaybackModeOption(BackgroundImagePlaybackMode.Sequential, Resource("Plugins.BackgroundImage.Mode.Sequential")),
            new PlaybackModeOption(BackgroundImagePlaybackMode.Random, Resource("Plugins.BackgroundImage.Mode.Random")),
        ];
        SelectedPlaybackMode = PlaybackModes.First(option => option.Mode == mainViewModel.BackgroundImagePlaybackMode);
        RefreshPlugins();
        OnPropertyChanged(nameof(LogPackagePluginEnabled));
    }

    public void SyncFromMainViewModel()
    {
        _syncingFromMainViewModel = true;
        try
        {
            BackgroundImageEnabled = _mainViewModel.BackgroundImageEnabled;
            BackgroundImagePath = _mainViewModel.BackgroundImagePath;
            BackgroundImageFolderPath = _mainViewModel.BackgroundImageFolderPath;
            BackgroundImageIntervalSeconds = _mainViewModel.BackgroundImageIntervalSeconds;
            BackgroundImageOpacity = _mainViewModel.BackgroundImageOpacity;
            SelectedPlaybackMode = PlaybackModes.First(option => option.Mode == _mainViewModel.BackgroundImagePlaybackMode);
            OnPropertyChanged(nameof(BackgroundImageSource));
            OnPropertyChanged(nameof(LogPackagePluginEnabled));
        }
        finally
        {
            _syncingFromMainViewModel = false;
        }
    }

    public string PluginDirectory { get; }

    public ObservableCollection<PluginRow> Plugins { get; } = [];

    public IReadOnlyList<PlaybackModeOption> PlaybackModes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBackgroundPluginSelected))]
    [NotifyPropertyChangedFor(nameof(IsLogPackagePluginSelected))]
    [NotifyPropertyChangedFor(nameof(IsExternalPluginSelected))]
    public partial PluginRow? SelectedPlugin { get; set; }

    public bool IsBackgroundPluginSelected => SelectedPlugin?.Kind == PluginKind.BackgroundImage;

    public bool IsLogPackagePluginSelected => SelectedPlugin?.Kind == PluginKind.LogPackage;

    public bool IsExternalPluginSelected => SelectedPlugin?.Kind == PluginKind.External;

    public bool LogPackagePluginEnabled => _mainViewModel.LogPackagePluginEnabled;

    [ObservableProperty]
    public partial bool BackgroundImageEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundImageSource))]
    public partial string BackgroundImagePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackgroundImageFolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial PlaybackModeOption? SelectedPlaybackMode { get; set; }

    [ObservableProperty]
    public partial int BackgroundImageIntervalSeconds { get; set; } = 300;

    [ObservableProperty]
    public partial double BackgroundImageOpacity { get; set; } = 0.18d;

    public ImageSource? BackgroundImageSource => _mainViewModel.BackgroundImageSource;

    [RelayCommand]
    private void OpenPluginFolder()
    {
        Directory.CreateDirectory(PluginDirectory);
        Process.Start(new ProcessStartInfo(PluginDirectory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void RefreshPlugins()
    {
        Directory.CreateDirectory(PluginDirectory);
        string? selectedId = SelectedPlugin?.Id;
        Plugins.Clear();
        Plugins.Add(new PluginRow("background-image", Resource("Plugins.BackgroundImage.Name"), "Built-in", Resource("Plugins.BackgroundImage.Description"), string.Empty, PluginKind.BackgroundImage));
        Plugins.Add(new PluginRow("log-package", Resource("Plugins.LogPackage.Name"), "Built-in", Resource("Plugins.LogPackage.Description"), string.Empty, PluginKind.LogPackage));
        foreach (string file in Directory.GetFiles(PluginDirectory, "*.dll"))
        {
            string version = "Unknown";
            try
            {
                version = AssemblyName.GetAssemblyName(file).Version?.ToString() ?? version;
            }
            catch (BadImageFormatException)
            {
                version = "Invalid .NET assembly";
            }

            Plugins.Add(new PluginRow(file, Path.GetFileNameWithoutExtension(file), version, Resource("Plugins.External.Description"), file, PluginKind.External));
        }

        SelectedPlugin = Plugins.FirstOrDefault(plugin => string.Equals(plugin.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? Plugins.FirstOrDefault();
    }

    [RelayCommand]
    private void SelectBackgroundImage()
    {
        OpenFileDialog dialog = new() { Filter = Resource("Plugins.BackgroundImage.Filter"), CheckFileExists = true };
        if (dialog.ShowDialog() == true)
        {
            _mainViewModel.ConfigureSingleBackgroundImage(dialog.FileName);
            SyncFromMainViewModel();
        }
    }

    [RelayCommand]
    private void SelectBackgroundImageFolder()
    {
        OpenFolderDialog dialog = new()
        {
            Title = Resource("Plugins.BackgroundImage.SelectFolder"),
            InitialDirectory = Directory.Exists(BackgroundImageFolderPath) ? BackgroundImageFolderPath : null,
        };
        if (dialog.ShowDialog() == true)
        {
            _mainViewModel.ConfigureBackgroundImageFolder(dialog.FolderName);
            SyncFromMainViewModel();
        }
    }

    [RelayCommand]
    private void ClearBackgroundImage()
    {
        BackgroundImageEnabled = false;
        BackgroundImagePath = string.Empty;
        BackgroundImageFolderPath = string.Empty;
    }

    [RelayCommand]
    private void ToggleBackgroundImage()
    {
        _mainViewModel.SetBackgroundImagePluginEnabled(!_mainViewModel.BackgroundImageEnabled);
        SyncFromMainViewModel();
    }

    [RelayCommand]
    private void ToggleLogPackage()
    {
        _mainViewModel.SetLogPackagePluginEnabled(!_mainViewModel.LogPackagePluginEnabled);
        OnPropertyChanged(nameof(LogPackagePluginEnabled));
    }

    [RelayCommand]
    private void ShowNextBackgroundImage()
    {
        _mainViewModel.ShowNextBackgroundImage();
        OnPropertyChanged(nameof(BackgroundImageSource));
    }

    partial void OnBackgroundImageEnabledChanged(bool value)
    {
        if (_syncingFromMainViewModel)
        {
            return;
        }

        _mainViewModel.BackgroundImageEnabled = value;
        Program.DiagnosticLog?.Information($"Background image plugin {(value ? "enabled" : "disabled")}.");
        OnPropertyChanged(nameof(BackgroundImageSource));
    }

    partial void OnBackgroundImagePathChanged(string value)
    {
        if (_syncingFromMainViewModel)
        {
            return;
        }

        _mainViewModel.BackgroundImagePath = value;
        OnPropertyChanged(nameof(BackgroundImageSource));
    }

    partial void OnBackgroundImageFolderPathChanged(string value)
    {
        if (!_syncingFromMainViewModel)
        {
            _mainViewModel.BackgroundImageFolderPath = value;
        }
    }

    partial void OnSelectedPlaybackModeChanged(PlaybackModeOption? value)
    {
        if (!_syncingFromMainViewModel && value is not null)
        {
            _mainViewModel.BackgroundImagePlaybackMode = value.Mode;
            Program.DiagnosticLog?.Information($"Background image playback mode changed. Mode={value.Mode}.");
            OnPropertyChanged(nameof(BackgroundImageSource));
        }
    }

    partial void OnBackgroundImageIntervalSecondsChanged(int value) =>
        SyncBackgroundImageInterval(value);

    private void SyncBackgroundImageInterval(int value)
    {
        if (!_syncingFromMainViewModel)
        {
            SetBackgroundImageInterval(value);
        }
    }

    private void SetBackgroundImageInterval(int value)
    {
        int interval = Math.Clamp(value, 1, 86_400);
        _mainViewModel.BackgroundImageIntervalSeconds = interval;
        Program.DiagnosticLog?.Information($"Background image interval changed. Seconds={interval}.");
    }

    partial void OnBackgroundImageOpacityChanged(double value)
    {
        if (!_syncingFromMainViewModel)
        {
            _mainViewModel.BackgroundImageOpacity = Math.Clamp(value, 0d, 1d);
        }
    }

    private static string Resource(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public sealed record PluginRow(string Id, string Name, string Version, string Description, string Path, PluginKind Kind);

    public enum PluginKind
    {
        BackgroundImage,
        LogPackage,
        External,
    }

    public sealed record PlaybackModeOption(BackgroundImagePlaybackMode Mode, string DisplayName);
}
