using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DuCom.Controls;
using DuCom.Core.Diagnostics;
using DuCom.Services;
using Microsoft.Win32;

namespace DuCom;

public partial class VariablePlotWindow : IDisposable
{
    private readonly VariableMonitorService _monitor;
    private readonly Action<VariableMonitorConfiguration> _applyConfiguration;
    private readonly AnalysisWindowPreferencesService _preferences;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _displayPaused;
    private bool _disposed;
    private VariableMonitorConfiguration _configuration;

    public VariablePlotWindow(VariableMonitorService monitor, Action<VariableMonitorConfiguration> applyConfiguration,
        AnalysisWindowPreferencesService preferences)
    {
        InitializeComponent();
        _monitor = monitor;
        _applyConfiguration = applyConfiguration;
        _preferences = preferences;
        DataContext = this;
        _configuration = VariableMonitorRuleStore.LoadConfiguration();
        ApplyPreferences(_preferences.Load().VariablePlot);
        FollowCheck.IsChecked = _configuration.Plot.FollowLatest;
        SelectWindow(_configuration.Plot.VisibleWindowSeconds);
        _timer.Tick += OnTick;
        _timer.Start();
        Closed += (_, _) => Dispose();
    }

    public ObservableCollection<VariablePlotSeriesRow> Series { get; } = [];

    private void OnTick(object? sender, EventArgs e)
    {
        if (_displayPaused) return;
        VariablePlotSnapshot snapshot = _monitor.GetPlotSnapshot();
        Dictionary<Guid, VariablePlotSeriesRow> current = Series.ToDictionary(row => row.Id);
        foreach (VariableSeriesSnapshot series in snapshot.Series)
        {
            if (!current.TryGetValue(series.Rule.Id, out VariablePlotSeriesRow? row))
            {
                row = new VariablePlotSeriesRow(series.Rule);
                row.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(VariablePlotSeriesRow.IsVisible)) Render(snapshot); };
                Series.Add(row);
            }
            row.Update(series);
        }
        foreach (VariablePlotSeriesRow removed in Series.Where(row => snapshot.Series.All(item => item.Rule.Id != row.Id)).ToArray()) Series.Remove(removed);
        Render(snapshot);
        long evicted = snapshot.Series.Sum(item => item.EvictedSampleCount);
        StatusText.Text = string.Format(CultureInfo.CurrentCulture, Resource("VariablePlot.StatusFormat"), snapshot.Series.Count, snapshot.Gaps.Count, evicted);
    }

    private void Render(VariablePlotSnapshot snapshot)
    {
        DateTimeOffset end = snapshot.Series.SelectMany(series => series.Samples).Select(sample => sample.SampledAtUtc).DefaultIfEmpty(DateTimeOffset.UtcNow).Max();
        int seconds = SelectedWindowSeconds();
        DateTimeOffset start = end.AddSeconds(-seconds);
        List<PlotRenderSeries> rendered = [];
        foreach (VariableSeriesSnapshot source in snapshot.Series)
        {
            VariablePlotSeriesRow? row = Series.FirstOrDefault(item => item.Id == source.Rule.Id);
            if (row is not { IsVisible: true }) continue;
            VariableNumericSample[] visible = source.Samples.Where(sample => sample.SampledAtUtc >= start && sample.SampledAtUtc <= end).ToArray();
            IReadOnlyList<VariableNumericSample> downsampled = VariablePlotDownsampler.MinMax(visible, _configuration.Plot.MaximumRenderedPointsPerSeries);
            Color color = (Color)ColorConverter.ConvertFromString(source.Rule.Color ?? "#42A5F5");
            rendered.Add(new PlotRenderSeries(source.Rule.Name, source.Rule.Unit ?? string.Empty, source.Rule.AxisId ?? "default", color,
                downsampled.Select(sample => new PlotRenderPoint(sample.SampledAtUtc, sample.NumericValue)).ToArray()));
        }
        Plot.SetSeries(rendered);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _displayPaused = !_displayPaused;
        PauseButton.Content = Resource(_displayPaused ? "Analysis.Resume" : "Analysis.Pause");
    }
    private void Clear_Click(object sender, RoutedEventArgs e) => _monitor.ClearPlot();
    private void ResetView_Click(object sender, RoutedEventArgs e) => Plot.ResetView();
    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new() { Filter = "CSV (*.csv)|*.csv", FileName = "ducom-variable-plot.csv" };
        if (dialog.ShowDialog(this) != true) return;
        VariablePlotSnapshot snapshot = _monitor.GetPlotSnapshot();
        await Task.Run(() => File.WriteAllText(dialog.FileName, VariablePlotCsv.ToLongTable(snapshot), new System.Text.UTF8Encoding(false)));
    }
    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadConfiguration();
    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(VariableMonitorRuleStore.FilePath)) VariableMonitorRuleStore.ResetDefaults();
        Process.Start(new ProcessStartInfo(VariableMonitorRuleStore.FilePath) { UseShellExecute = true });
    }
    private void ResetConfig_Click(object sender, RoutedEventArgs e) { VariableMonitorRuleStore.ResetDefaults(); ReloadConfiguration(); }
    private void ReloadConfiguration()
    {
        VariableMonitorConfiguration loaded = VariableMonitorRuleStore.LoadConfiguration();
        if (loaded.Diagnostics.Count > 0 && loaded.Rules.Count == 0) { StatusText.Text = string.Join(" ", loaded.Diagnostics); return; }
        _configuration = loaded;
        _applyConfiguration(loaded);
    }

    private void ApplyPreferences(AnalysisWindowPreference value)
    {
        Width = value.Width; Height = value.Height; Topmost = value.Topmost;
        if (double.IsFinite(value.Left) && double.IsFinite(value.Top)) { Left = value.Left; Top = value.Top; WindowStartupLocation = WindowStartupLocation.Manual; }
    }
    private void SelectWindow(int seconds)
    {
        foreach (ComboBoxItem item in WindowCombo.Items) if (int.TryParse(item.Tag?.ToString(), out int value) && value == seconds) { WindowCombo.SelectedItem = item; return; }
        WindowCombo.SelectedIndex = 1;
    }
    private int SelectedWindowSeconds() => WindowCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int value) ? value : 30;
    private string Resource(string key) => TryFindResource(key) as string ?? key;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTick;
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        AnalysisWindowPreferences all = _preferences.Load();
        _preferences.Save(all with { VariablePlot = new AnalysisWindowPreference(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            Topmost, VisibleWindowSeconds: SelectedWindowSeconds(), FollowLatest: FollowCheck.IsChecked == true) });
        GC.SuppressFinalize(this);
    }
}

public sealed class VariablePlotSeriesRow : INotifyPropertyChanged
{
    private bool _isVisible = true;
    public VariablePlotSeriesRow(VariableMonitorRule rule) { Id = rule.Id; Name = rule.Name; Unit = rule.Unit ?? string.Empty; Color = rule.Color ?? "#42A5F5"; AxisId = rule.AxisId ?? "default"; }
    public Guid Id { get; }
    public string Name { get; }
    public string Unit { get; }
    public string Color { get; }
    public string AxisId { get; }
    public string CurrentValue { get; private set; } = "--";
    public bool IsVisible { get => _isVisible; set { if (_isVisible == value) return; _isVisible = value; PropertyChanged?.Invoke(this, new(nameof(IsVisible))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(VariableSeriesSnapshot series)
    {
        string value = series.Samples.Count == 0 ? "--" : series.Samples[^1].NumericValue.ToString("G6", CultureInfo.CurrentCulture);
        if (value == CurrentValue) return; CurrentValue = value; PropertyChanged?.Invoke(this, new(nameof(CurrentValue)));
    }
}
