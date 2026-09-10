using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Services;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    public partial string LogDirectory { get; set; } = GetDefaultLogDirectory();

    [ObservableProperty]
    public partial int LogRotationMegabytes { get; set; } = 40;

    [ObservableProperty]
    public partial bool LogRotationEnabled { get; set; } = true;

    [ObservableProperty]
    public partial int DisplayBudgetMegabytes { get; set; } = 64;

    [ObservableProperty]
    public partial string LogFileNameFormat { get; set; } = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}";

    public string LogFileNamePreview => PreviewLogFileName(LogFileNameFormat, SelectedSession?.PortName ?? SelectedPort ?? "COM31");

    [RelayCommand]
    private void OpenLogFolder(SessionViewModel? session)
    {
        IWorkspaceSession? workspaceSession = (session ?? SelectedSession ?? SelectedRightSession)?.WorkspaceSession;
        string directory = workspaceSession?.LogDirectory ?? LogDirectory;
        Directory.CreateDirectory(directory);
        if (workspaceSession?.CurrentLogFilePath is { } currentLogFile && File.Exists(currentLogFile))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{currentLogFile}\"") { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void SelectLogDirectory()
    {
        OpenFolderDialog dialog = new()
        {
            Title = (string?)Application.Current.TryFindResource("Settings.SelectLogDirectory") ?? "Select log directory",
            InitialDirectory = Directory.Exists(LogDirectory) ? LogDirectory : null,
        };
        if (dialog.ShowDialog() == true)
        {
            LogDirectory = dialog.FolderName;
        }
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        OpenFileDialog dialog = new()
        {
            Filter = "Text logs (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(LogDirectory) ? LogDirectory : null,
        };
        if (dialog.ShowDialog() == true)
        {
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task SaveVisibleLogAsync()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "Text logs (*.txt)|*.txt",
            FileName = $"{session.PortName}-visible.txt",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] lines = [.. session.VisibleLines.Select(line => line.Text)];
        await Task.Run(() => File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(false)));
    }

    [RelayCommand]
    private async Task SaveVisibleLogAsHexAsync()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "HEX text (*.txt)|*.txt",
            FileName = $"{session.PortName}-visible.hex.txt",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] lines = [.. session.VisibleLines.Select(line => line.Text)];
        Encoding encoding = GetSessionEncoding(session);
        await Task.Run(() =>
        {
            string[] hexLines = [.. lines.Select(line => DuCom.Core.Sending.HexRepresentation.ToHexText(encoding.GetBytes(line)))];
            File.WriteAllLines(dialog.FileName, hexLines, new UTF8Encoding(false));
        });
    }

    [RelayCommand]
    private async Task SaveVisibleLogAsBinaryAsync()
    {
        SessionViewModel? session = SelectedSession;
        if (session is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "Binary (*.bin)|*.bin",
            FileName = $"{session.PortName}-visible.bin",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string[] lines = [.. session.VisibleLines.Select(line => line.Text)];
        Encoding encoding = GetSessionEncoding(session);
        await Task.Run(() =>
        {
            using FileStream stream = new(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
            foreach (string line in lines)
            {
                byte[] payload = encoding.GetBytes(line);
                stream.Write(payload, 0, payload.Length);
                stream.WriteByte((byte)'\n');
            }
        });
    }

    private static Encoding GetSessionEncoding(SessionViewModel session)
    {
        try
        {
            return Encoding.GetEncoding(session.WorkspaceSession.Settings.EncodingName);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static string GetDefaultLogDirectory() => Path.Combine(AppContext.BaseDirectory, "Logs");

    private static string PreviewLogFileName(string format, string portName)
    {
        DateTimeOffset sample = new(2026, 8, 31, 10, 7, 42, 813, TimeSpan.Zero);
        string value = (string.IsNullOrWhiteSpace(format) ? "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}" : format)
            .Replace("{Port}", portName, StringComparison.Ordinal)
            .Replace("{yyyy}", sample.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", sample.ToString("MM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", sample.ToString("dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HH}", sample.ToString("HH", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{mm}", sample.ToString("mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{ss}", sample.ToString("ss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{fff}", sample.ToString("fff", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{yyyyMMdd}", sample.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HHmmss}", sample.ToString("HHmmss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Segment}", "0000", StringComparison.Ordinal);
        return string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)) + ".txt";
    }
}
