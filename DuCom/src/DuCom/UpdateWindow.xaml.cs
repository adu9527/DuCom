using System.Windows;
using DuCom.Services.Updates;
using DuCom.ViewModels;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class UpdateWindow : FluentWindow
{
    private readonly UpdateViewModel _viewModel = UpdateViewModel.Instance;
    private readonly bool _downloadOnLoad;
    private bool _checkedOnLoad;

    private UpdateWindow(bool downloadOnLoad)
    {
        _downloadOnLoad = downloadOnLoad;
        InitializeComponent();
        DataContext = _viewModel;
        Loaded += UpdateWindow_Loaded;
    }

    public static bool IsOpen => Application.Current.Windows.OfType<UpdateWindow>().Any();

    public static void Show(Window? owner, bool downloadOnLoad = false)
    {
        UpdateWindow? existing = Application.Current.Windows.OfType<UpdateWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        UpdateWindow window = new(downloadOnLoad)
        {
            Owner = owner?.IsLoaded == true ? owner : Application.Current?.MainWindow,
        };
        window.Show();
    }

    private async void UpdateWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= UpdateWindow_Loaded;
        if (_checkedOnLoad)
        {
            return;
        }

        _checkedOnLoad = true;
        if (AppUpdateService.Instance.Phase == UpdatePhase.Idle)
        {
            await _viewModel.CheckCommand.ExecuteAsync(null);
        }

        if (_downloadOnLoad && AppUpdateService.Instance.Phase == UpdatePhase.Available)
        {
            await _viewModel.DownloadCommand.ExecuteAsync(null);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ReleasePage_Click(object sender, RoutedEventArgs e) => _viewModel.OpenReleasePageCommand.Execute(null);
}
