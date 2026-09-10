using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DuCom.ViewModels;

public partial class PluginManagerViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;

    public PluginManagerViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
        if (mainViewModel.PluginSystem is { } system)
        {
            AttachPluginSystem(system);
        }

        RefreshPlugins();
    }

    private static string Resource(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
