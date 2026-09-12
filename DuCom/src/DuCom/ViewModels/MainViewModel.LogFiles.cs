using System.Diagnostics;
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
    public string LogFileNamePreview => LogFilePolicy.PreviewName(LogFileNameFormat, Workspace.SelectedSession?.PortName ?? SelectedPort ?? "COM31");

    [RelayCommand]
    private void OpenLogFolder(SessionViewModel? session)
    {
        IWorkspaceSession? workspaceSession = (session ?? Workspace.SelectedSession ?? Workspace.SelectedRightSession)?.WorkspaceSession;
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
        SessionViewModel? session = Workspace.SelectedSession;
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
        SessionViewModel? session = Workspace.SelectedSession;
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
        Encoding encoding = LogFilePolicy.GetEncoding(session);
        await Task.Run(() =>
        {
            string[] hexLines = [.. lines.Select(line => DuCom.Core.Sending.HexRepresentation.ToHexText(encoding.GetBytes(line)))];
            File.WriteAllLines(dialog.FileName, hexLines, new UTF8Encoding(false));
        });
    }

    [RelayCommand]
    private async Task SaveVisibleLogAsBinaryAsync()
    {
        SessionViewModel? session = Workspace.SelectedSession;
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
        Encoding encoding = LogFilePolicy.GetEncoding(session);
        await Task.Run(() => LogFilePolicy.WriteBinary(dialog.FileName, lines, encoding));
    }
}
