using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DuCom.Core.Logging;
using DuCom.Services;
using DuCom.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class LogPackageWindow : FluentWindow
{
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(31) };
    private readonly LogPackageViewModel _viewModel;
    private readonly Action<string> _saveOutputDirectory;

    internal LogPackageWindow(IEnumerable<SessionViewModel> sessions, string outputDirectory, Action<string> saveOutputDirectory)
    {
        InitializeComponent();
        _saveOutputDirectory = saveOutputDirectory;
        LogPackagePreferences preferences = LogPackagePreferencesService.Load();
        _viewModel = new LogPackageViewModel(sessions, outputDirectory, preferences, SelectOutputDirectory);
        _viewModel.CreatePackageAsync = CreatePackageAsync;
        DataContext = _viewModel;
        ApplyPlacement(preferences);
        UpdateClock();
        _viewModel.ReproductionTime = _viewModel.CurrentTime;
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
    }

    private void UpdateClock() => _viewModel.CurrentTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private void SelectOutputDirectory()
    {
        OpenFolderDialog dialog = new()
        {
            Title = Resource("LogPackage.SelectOutputDirectory"),
            InitialDirectory = Directory.Exists(_viewModel.OutputDirectory) ? _viewModel.OutputDirectory : null,
        };
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.OutputDirectory = dialog.FolderName;
        }
    }

    private async Task CreatePackageAsync()
    {
        LogPackagePortViewModel[] selected = [.. _viewModel.Ports.Where(port => port.IsSelected)];
        if (selected.Length == 0)
        {
            ThemedMessageDialog.Show(this, Resource("LogPackage.Validation.NoPort"), Resource("LogPackage.Title"), ThemedMessageDialogKind.Warning);
            return;
        }

        List<string> missingFields = [];
        AddMissingField(_viewModel.ProjectName, "LogPackage.Project");
        AddMissingField(_viewModel.Title, "LogPackage.ProblemTitle");
        AddMissingField(_viewModel.Tester, "LogPackage.Tester");
        AddMissingField(_viewModel.OutputDirectory, "LogPackage.OutputDirectory");
        if (missingFields.Count > 0)
        {
            string fields = string.Join(Resource("LogPackage.Validation.Separator"), missingFields);
            string message = Resource("LogPackage.Validation.RequiredFields").Replace("{0}", fields, StringComparison.Ordinal);
            ThemedMessageDialog.Show(this, message, Resource("LogPackage.Title"), ThemedMessageDialogKind.Warning);
            return;
        }

        void AddMissingField(string value, string resourceKey)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missingFields.Add(Resource(resourceKey).TrimEnd(' ', '*'));
            }
        }

        _viewModel.IsBusy = true;
        _viewModel.Status = Resource("LogPackage.Snapshotting");
        try
        {
            List<LogPackagePort> ports = [];
            foreach (LogPackagePortViewModel port in selected)
            {
                IReadOnlyList<SessionLogFileSnapshot> files = await port.Session.WorkspaceSession.CreateLogSnapshotAsync();
                ports.Add(new LogPackagePort(port.PortName, port.DeviceName.Trim(), files));
            }

            SessionLogFileSnapshot[] largeFiles = [.. ports.SelectMany(port => port.Files).Where(file => file.Length > LogPackageService.LargeFileWarningBytes)];
            if (largeFiles.Length > 0)
            {
                string details = string.Join(Environment.NewLine, largeFiles.Select(file => $"{Path.GetFileName(file.Path)} ({file.Length / 1024d / 1024d:F1} MiB)"));
                if (!ThemedMessageDialog.Confirm(
                        this,
                        Resource("LogPackage.LargeFileWarning").Replace("{0}", details, StringComparison.Ordinal),
                        Resource("LogPackage.LargeFileTitle"),
                        "LogPackage.Continue",
                        "LogPackage.Cancel"))
                {
                    return;
                }
            }

            _viewModel.Status = Resource("LogPackage.Compressing");
            LogPackageRequest request = new(
                _viewModel.OutputDirectory.Trim(), _viewModel.ProjectName.Trim(), _viewModel.Title.Trim(), _viewModel.Tester.Trim(),
                _viewModel.DeviceSoftwareVersion.Trim(), _viewModel.ReproductionProbability.Trim(), _viewModel.ReproductionTime.Trim(), _viewModel.ProblemDescription.Trim(), _viewModel.ReproductionSteps.Trim(), _viewModel.Notes.Trim(),
                DateTimeOffset.Now, ports);
            string path = await LogPackageService.CreateAsync(request);
            _viewModel.Status = Resource("LogPackage.Success").Replace("{0}", path, StringComparison.Ordinal);
            ThemedMessageDialog.Show(this, _viewModel.Status, Resource("LogPackage.Title"), ThemedMessageDialogKind.Information);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Error("Log package creation failed.", exception);
            ThemedMessageDialog.Show(this, Resource("LogPackage.Failed").Replace("{0}", exception.Message, StringComparison.Ordinal), Resource("LogPackage.Title"), ThemedMessageDialogKind.Error);
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            _viewModel.Status = Resource("LogPackage.CloseWhileBusy");
            return;
        }

        _clock.Stop();
        if (!string.IsNullOrWhiteSpace(_viewModel.OutputDirectory))
        {
            _saveOutputDirectory(_viewModel.OutputDirectory.Trim());
        }
        Rect bounds = RestoreBounds;
        LogPackagePreferencesService.Save(new LogPackagePreferences(
            _viewModel.ProjectName.Trim(), _viewModel.Tester.Trim(),
            _viewModel.Ports.ToDictionary(port => port.PortName, port => port.DeviceName.Trim(), StringComparer.OrdinalIgnoreCase),
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized,
            _viewModel.Title.Trim(), _viewModel.DeviceSoftwareVersion.Trim(), _viewModel.ReproductionProbability.Trim(), _viewModel.ProblemDescription.Trim(),
            _viewModel.ReproductionSteps.Trim(), _viewModel.Notes.Trim()));
    }

    private void ApplyPlacement(LogPackagePreferences preferences)
    {
        double maxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width - 40);
        double maxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40);
        Width = Math.Clamp(double.IsFinite(preferences.Width) ? preferences.Width : 1080, MinWidth, maxWidth);
        Height = Math.Clamp(double.IsFinite(preferences.Height) ? preferences.Height : 900, MinHeight, maxHeight);
        if (double.IsFinite(preferences.Left) && double.IsFinite(preferences.Top))
        {
            Left = Math.Clamp(preferences.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
            Top = Math.Clamp(preferences.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        }
        else
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
        }

        if (preferences.Maximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static string Resource(string key) => Application.Current?.TryFindResource(key) as string ?? key;
}
