using System.IO;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private static string ShortcutsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuCom",
        "shortcuts.json");

    private void LoadShortcuts()
    {
        if (ShortcutManager.TryLoad(ShortcutsFilePath))
        {
            return;
        }

        Program.DiagnosticLog?.Warning($"Failed to load shortcuts from {ShortcutsFilePath}; using defaults.");
    }
}
