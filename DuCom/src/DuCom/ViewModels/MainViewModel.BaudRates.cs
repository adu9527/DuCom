using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private static readonly int[] DefaultBaudRates = [9_600, 19_200, 115_200, 921_600, 1_152_000, 1_500_000, 2_000_000, 3_000_000];

    public ObservableCollection<int> BaudRates { get; } = [.. DefaultBaudRates];

    [ObservableProperty]
    public partial int NewBaudRate { get; set; }

    internal async Task ApplySessionBaudRateAsync(SessionViewModel session, int baudRate)
    {
        if (session.IsBusy || session.BaudRate == baudRate)
        {
            return;
        }

        SerialPortSettings updated = session.WorkspaceSession.Settings with { BaudRate = baudRate };
        await ApplyPortSettingsAsync(session, updated);
    }

    [RelayCommand]
    private void AddBaudRate()
    {
        if (NewBaudRate <= 0 || BaudRates.Contains(NewBaudRate))
        {
            return;
        }

        BaudRates.Add(NewBaudRate);
        List<int> ordered = [.. BaudRates.Order()];
        BaudRates.Clear();
        foreach (int value in ordered)
        {
            BaudRates.Add(value);
        }

        NewBaudRate = 0;
        MarkSettingsDirty();
    }

    [RelayCommand]
    private void RemoveBaudRate(int value)
    {
        bool isUsed = value == BaudRate || value == SerialParameterBaudRate ||
            Sessions.Any(session => session.BaudRate == value);
        if (BaudRates.Count > 1 && !isUsed)
        {
            BaudRates.Remove(value);
            MarkSettingsDirty();
        }
    }

    private void EnsureActiveBaudRatesPresent()
    {
        EnsureBaudRatePresent(BaudRate);
        EnsureBaudRatePresent(SerialParameterBaudRate);
        foreach (SessionViewModel session in Sessions)
        {
            EnsureBaudRatePresent(session.BaudRate);
        }
    }

    private void RestoreDefaultBaudRates()
    {
        HashSet<int> desired = [.. DefaultBaudRates, .. Sessions.Select(session => session.BaudRate)];
        foreach (int value in BaudRates.Where(value => !desired.Contains(value)).ToArray())
        {
            BaudRates.Remove(value);
        }

        foreach (int value in desired.Order())
        {
            if (!BaudRates.Contains(value))
            {
                BaudRates.Add(value);
            }
        }

        int[] ordered = [.. BaudRates.Order()];
        for (int targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            int currentIndex = BaudRates.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex)
            {
                BaudRates.Move(currentIndex, targetIndex);
            }
        }
    }

    private void EnsureBaudRatePresent(int baudRate)
    {
        if (baudRate <= 0 || BaudRates.Contains(baudRate))
        {
            return;
        }

        BaudRates.Add(baudRate);
        List<int> ordered = [.. BaudRates.Order()];
        BaudRates.Clear();
        foreach (int value in ordered)
        {
            BaudRates.Add(value);
        }
    }

    partial void OnBaudRateChanged(int value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }
}
