using DuCom.Core.Persistence;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private void LoadSettings()
    {
        ConfigurationSnapshot? snapshot = AppSettingsService.LoadWithReport<ConfigurationSnapshot>(out IReadOnlyList<string> skippedFields);
        if (snapshot is not null)
        {
            if (snapshot.SchemaVersion > CurrentSettingsSchemaVersion)
            {
                Program.DiagnosticLog?.Warning($"Settings were written by a newer DuCom (schema {snapshot.SchemaVersion} > {CurrentSettingsSchemaVersion}); unreadable values fall back to defaults.");
            }

            if (skippedFields.Count > 0)
            {
                Program.DiagnosticLog?.Warning($"Skipped {skippedFields.Count} unreadable settings field(s): {string.Join(", ", skippedFields)}");
            }

            ApplyConfiguration(snapshot);
            Program.DiagnosticLog?.Information($"Loaded settings from {AppSettingsService.SettingsFilePath}.");
        }
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        try
        {
            AppSettingsService.Save(CaptureConfiguration());
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to save settings.", exception);
        }
    }

    /// <summary>Settings schema version this build writes and understands; bump when persisted fields change shape.</summary>
    internal const int CurrentSettingsSchemaVersion = 1;

    private void OnSettingsSaveTick(object? sender, EventArgs e)
    {
        if (_settingsDirty)
        {
            _settingsDirty = false;
            SaveSettings();
        }
    }

    private void MarkSettingsDirty()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _settingsDirty = true;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }
}
