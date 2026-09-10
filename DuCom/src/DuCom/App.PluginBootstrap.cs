using System.IO;

namespace DuCom;

public partial class App
{
    private void InitializePluginSystem()
    {
        if (_compositionRoot is null)
        {
            return;
        }

        try
        {
            ViewModels.MainViewModel mainViewModel = _compositionRoot.MainViewModel;
            Services.Plugins.PluginSystemHost pluginSystem = new(
                () => mainViewModel.Sessions.Concat(mainViewModel.RightSessions),
                () => mainViewModel.AvailablePorts,
                _compositionRoot.SerialLeases,
                logDirectoryProvider: () => mainViewModel.LogDirectory);
            mainViewModel.AttachPluginSystem(pluginSystem);
            _pluginSystemHost = pluginSystem;
            string legacySettings = ReadSettingsText();
            string? legacyPreferences = ReadFileIfExists(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DuCom",
                "log-package-preferences.json"));
            _ = pluginSystem.InitializeAsync(legacySettings, legacyPreferences);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Plugin system startup failed; the core continues without plugins.", exception);
        }
    }

    private static string ReadSettingsText()
    {
        try
        {
            string path = Services.AppSettingsService.SettingsFilePath;
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? ReadFileIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
