using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Services;
using Microsoft.Win32;

namespace DuCom.ViewModels;

public partial class ToolCenterViewModel
{
    public ObservableCollection<string> VirtualPorts { get; } = [];

    public ObservableCollection<DuCom.Core.Processes.Com0ComPortPair> Com0ComPairs { get; } = [];

    [ObservableProperty]
    public partial DuCom.Core.Processes.Com0ComPortPair? SelectedCom0ComPair { get; set; }

    [ObservableProperty]
    public partial string NewPairPortA { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPairPortB { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool PairEmuBR { get; set; }

    [ObservableProperty]
    public partial bool PairEmuOverrun { get; set; }

    [ObservableProperty]
    public partial bool PairHiddenMode { get; set; }

    [ObservableProperty]
    public partial bool PairPlugInMode { get; set; }

    [ObservableProperty]
    public partial bool PairExclusiveMode { get; set; }

    [ObservableProperty]
    public partial string PairEmuNoise { get; set; } = "0";

    [ObservableProperty]
    public partial string PairRtto { get; set; } = "0";

    [ObservableProperty]
    public partial string PairRito { get; set; } = "0";

    [ObservableProperty]
    public partial string Com0ComStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCom0ComAvailable { get; private set; }

    [ObservableProperty]
    public partial string Com0ComPath { get; set; } = string.Empty;

    partial void OnSelectedCom0ComPairChanged(DuCom.Core.Processes.Com0ComPortPair? value)
    {
        if (value is null)
        {
            return;
        }

        NewPairPortA = value.SideA.PortName;
        NewPairPortB = value.SideB.PortName;
        IReadOnlyDictionary<string, string> options = value.SideA.Options;
        PairEmuBR = ReadCom0ComBoolean(options, "EmuBR");
        PairEmuOverrun = ReadCom0ComBoolean(options, "EmuOverrun");
        PairHiddenMode = ReadCom0ComBoolean(options, "HiddenMode");
        PairPlugInMode = ReadCom0ComBoolean(options, "PlugInMode");
        PairExclusiveMode = ReadCom0ComBoolean(options, "ExclusiveMode");
        PairEmuNoise = ReadCom0ComValue(options, "EmuNoise", "0");
        PairRtto = ReadCom0ComValue(options, "AddRTTO", "0");
        PairRito = ReadCom0ComValue(options, "AddRITO", "0");
    }

    private static bool ReadCom0ComBoolean(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string? value) &&
        value is not null &&
        (value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value == "1");

    private static string ReadCom0ComValue(IReadOnlyDictionary<string, string> options, string key, string fallback) =>
        options.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    [RelayCommand]
    private void RefreshVirtualPorts()
    {
        VirtualPorts.Clear();
        IEnumerable<string> ports = _mainViewModel?.DiscoveredPortNames ?? [];
        foreach (string port in ports.Order(StringComparer.OrdinalIgnoreCase))
        {
            VirtualPorts.Add(port);
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "com0com", "setupc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "com0com", "setupc.exe"),
        ];
        Com0ComPath = DuCom.Core.Processes.Com0ComParser.ResolveSetupcPath(
            candidates,
            Com0ComPreferencesService.LoadSetupcPath(),
            File.Exists);
        IsCom0ComAvailable = !string.IsNullOrEmpty(Com0ComPath);
    }

    [RelayCommand]
    private void BrowseCom0ComSetupc()
    {
        OpenFileDialog dialog = new()
        {
            Filter = GetResourceString("Tools.SetupcFileFilter"),
            CheckFileExists = true,
            FileName = "setupc.exe",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        SelectCom0ComPath(dialog.FileName);
    }

    [RelayCommand]
    private void UseCom0ComPath()
    {
        SelectCom0ComPath(Com0ComPath);
    }

    private void SelectCom0ComPath(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComPathInvalid");
            IsCom0ComAvailable = false;
            return;
        }

        if (!File.Exists(fullPath) || !Path.GetFileName(fullPath).Equals("setupc.exe", StringComparison.OrdinalIgnoreCase))
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComPathInvalid");
            IsCom0ComAvailable = false;
            return;
        }

        Com0ComPath = fullPath;
        IsCom0ComAvailable = true;
        Com0ComStatus = string.Empty;
        try
        {
            Com0ComPreferencesService.SaveSetupcPath(fullPath);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to save com0com path.", exception);
        }
    }

    [RelayCommand]
    private static void OpenCom0ComWebsite() =>
        Process.Start(new ProcessStartInfo("https://sourceforge.net/projects/com0com/") { UseShellExecute = true });

    [RelayCommand]
    private void OpenCom0ComFolder()
    {
        if (IsCom0ComAvailable)
        {
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(Com0ComPath)!) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private async Task RefreshCom0ComPairsAsync()
    {
        if (!IsCom0ComAvailable)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComMissing");
            return;
        }

        Com0ComCommandResult result = await Services.Com0ComService.RunAsync(Com0ComPath, "list");
        Com0ComPairs.Clear();
        if (result.Succeeded)
        {
            foreach (DuCom.Core.Processes.Com0ComPortPair pair in Services.Com0ComService.PairEntries(Services.Com0ComService.ParseList(result.Output)))
            {
                Com0ComPairs.Add(pair);
            }

            Com0ComStatus = string.Empty;
        }
        else
        {
            Com0ComStatus = result.Output;
            Program.DiagnosticLog?.Warning($"setupc list failed. {result.Output}");
        }
    }

    private string BuildPairOptions()
    {
        List<string> options =
        [
            $"EmuBR={(PairEmuBR ? "yes" : "no")}",
            $"EmuOverrun={(PairEmuOverrun ? "yes" : "no")}",
            $"HiddenMode={(PairHiddenMode ? "yes" : "no")}",
            $"PlugInMode={(PairPlugInMode ? "yes" : "no")}",
            $"ExclusiveMode={(PairExclusiveMode ? "yes" : "no")}",
        ];
        if (double.TryParse(PairEmuNoise, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double noise) && noise > 0)
        {
            options.Add($"EmuNoise={noise.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (long.TryParse(PairRtto, out long rtto) && rtto > 0)
        {
            options.Add($"AddRTTO={rtto}");
        }

        if (long.TryParse(PairRito, out long rito) && rito > 0)
        {
            options.Add($"AddRITO={rito}");
        }

        return string.Join(",", options);
    }

    [RelayCommand]
    private async Task InstallPairAsync()
    {
        if (!IsCom0ComAvailable)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComMissing");
            return;
        }

        if (!Services.Com0ComService.IsValidPortName(NewPairPortA) || !Services.Com0ComService.IsValidPortName(NewPairPortB))
        {
            Com0ComStatus = GetResourceString("Tools.InvalidPortPair");
            return;
        }

        string options = BuildPairOptions();
        string arguments = $"install PortName={NewPairPortA.Trim().ToUpperInvariant()},{options} PortName={NewPairPortB.Trim().ToUpperInvariant()},{options}";
        Com0ComCommandResult result = await Services.Com0ComService.RunAsync(Com0ComPath, arguments);
        Com0ComStatus = result.Output;
        Program.DiagnosticLog?.Information($"setupc install: {arguments}; success={result.Succeeded}");
        if (result.Succeeded)
        {
            NewPairPortA = string.Empty;
            NewPairPortB = string.Empty;
            await RefreshCom0ComPairsAsync();
            RefreshVirtualPorts();
        }
    }

    [RelayCommand]
    private async Task RemovePairAsync(DuCom.Core.Processes.Com0ComPortPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!IsCom0ComAvailable)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComMissing");
            return;
        }

        Com0ComCommandResult result = await Services.Com0ComService.RunAsync(Com0ComPath, $"remove {pair.PairNumber}");
        Com0ComStatus = result.Output;
        Program.DiagnosticLog?.Information($"setupc remove {pair.PairNumber}; success={result.Succeeded}");
        if (result.Succeeded)
        {
            await RefreshCom0ComPairsAsync();
            RefreshVirtualPorts();
        }
    }

    [RelayCommand]
    private async Task ApplyPairOptionsAsync(DuCom.Core.Processes.Com0ComPortPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        if (!IsCom0ComAvailable)
        {
            Com0ComStatus = GetResourceString("Tools.Com0ComMissing");
            return;
        }

        string options = BuildPairOptions();
        Com0ComCommandResult a = await Services.Com0ComService.RunAsync(Com0ComPath, $"change {pair.SideA.Id} {options}");
        Com0ComCommandResult b = await Services.Com0ComService.RunAsync(Com0ComPath, $"change {pair.SideB.Id} {options}");
        Com0ComStatus = (a.Output + " " + b.Output).Trim();
        Program.DiagnosticLog?.Information($"setupc change pair {pair.PairNumber}; successA={a.Succeeded}, successB={b.Succeeded}");
        if (a.Succeeded && b.Succeeded)
        {
            await RefreshCom0ComPairsAsync();
        }
    }
}
