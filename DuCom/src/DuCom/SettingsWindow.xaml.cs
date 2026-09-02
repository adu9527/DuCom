using System.Windows.Input;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class SettingsWindow : FluentWindow
{
    public bool IsTransportOnly { get; }

    public SettingsWindow(int selectedCategory = 0, bool transportOnly = false)
    {
        IsTransportOnly = transportOnly;
        InitializeComponent();
        CategoryList.SelectedIndex = selectedCategory;
        CategoryList.Visibility = transportOnly ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        CategoryColumn.Width = transportOnly ? new System.Windows.GridLength(0) : new System.Windows.GridLength(190);
        CategoryGapColumn.Width = transportOnly ? new System.Windows.GridLength(0) : new System.Windows.GridLength(12);
        if (transportOnly)
        {
            string title = System.Windows.Application.Current.TryFindResource("Settings.SerialParameters") as string ?? "Serial parameters";
            Title = title;
            SettingsTitleBar.Title = title;
            Loaded += (_, _) => RefreshTransportState();
            DataContextChanged += (_, _) => RefreshTransportState();
        }
    }

    internal void RefreshTransportState()
    {
        if (DataContext is ViewModels.MainViewModel viewModel)
        {
            SerialParameterInputs.IsEnabled = !IsTransportOnly || viewModel.IsSerialParametersEditable;
        }
    }

    internal void SelectCategory(int category) => CategoryList.SelectedIndex = category;

    internal void SetWindowTitle(string title)
    {
        Title = title;
        SettingsTitleBar.Title = title;
    }

    private void ShortcutsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid ||
            grid.SelectedItem is not ViewModels.ShortcutsSettingsViewModel.ShortcutRow row ||
            DataContext is not ViewModels.MainViewModel viewModel)
        {
            return;
        }

        if (viewModel.ShortcutsSettings.StartEditShortcutCommand.CanExecute(row))
        {
            viewModel.ShortcutsSettings.StartEditShortcutCommand.Execute(row);
        }
    }

    private void ShortcutInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.MainViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;
        viewModel.ShortcutsSettings.ApplyCapturedKeys(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
    }
}
