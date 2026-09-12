using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DuCom.Core.Ports;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<int> BaudRates { get; } = [.. BaudRateListPolicy.DefaultBaudRates];

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
        IReadOnlyList<int>? updated = BaudRateListPolicy.Add(BaudRates, NewBaudRate);
        if (updated is null)
        {
            return;
        }

        ApplyOrderedBaudRates(updated);
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
        ApplyOrderedBaudRates(BaudRateListPolicy.PruneToDefaults(Sessions.Select(session => session.BaudRate)));
    }

    private void EnsureBaudRatePresent(int baudRate)
    {
        if (baudRate > 0 && !BaudRates.Contains(baudRate))
        {
            ApplyOrderedBaudRates(BaudRateListPolicy.EnsurePresent(BaudRates, baudRate));
        }
    }

    /// <summary>
    /// Diffs the list into the target sequence with minimal churn, preserving item
    /// identity where possible so open ComboBox selections survive the refresh.
    /// </summary>
    private void ApplyOrderedBaudRates(IReadOnlyList<int> target)
    {
        foreach (int value in BaudRates.Where(value => !target.Contains(value)).ToArray())
        {
            BaudRates.Remove(value);
        }

        foreach (int value in target)
        {
            if (!BaudRates.Contains(value))
            {
                BaudRates.Add(value);
            }
        }

        for (int targetIndex = 0; targetIndex < target.Count; targetIndex++)
        {
            int currentIndex = BaudRates.IndexOf(target[targetIndex]);
            if (currentIndex != targetIndex)
            {
                BaudRates.Move(currentIndex, targetIndex);
            }
        }
    }

    partial void OnBaudRateChanged(int value)
    {
        MarkSettingsDirty();
        SchedulePortSettingsApply();
    }
}
