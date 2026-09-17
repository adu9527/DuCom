using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DuCom.Core.Protocols;
using DuCom.Services;
using DuCom.ViewModels;
using Microsoft.Win32;

namespace DuCom;

public partial class ProtocolDecoderWindow : IDisposable
{
    private readonly SessionWorkspaceViewModel _workspace;
    private readonly AnalysisWindowPreferencesService _preferences;
    private readonly ProtocolDecoderService _decoder = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private ProtocolDecoderConfiguration _configuration;
    private bool _displayPaused;
    private bool _disposed;

    public ProtocolDecoderWindow(SessionWorkspaceViewModel workspace, AnalysisWindowPreferencesService preferences)
    {
        InitializeComponent();
        _workspace = workspace;
        _preferences = preferences;
        DataContext = this;
        _configuration = ProtocolDecoderProfileStore.Load();
        ReloadChoices();
        ApplyPreferences(_preferences.Load().ProtocolDecoder);
        _timer.Tick += OnTick; _timer.Start();
        Closed += (_, _) => Dispose();
    }

    public ObservableCollection<ProtocolFrameRow> Frames { get; } = [];

    private void ReloadChoices()
    {
        string? port = (PortCombo.SelectedItem as SessionViewModel)?.PortName;
        PortCombo.ItemsSource = _workspace.Sessions.Where(session => session.IsOpen).ToArray();
        PortCombo.SelectedItem = _workspace.Sessions.FirstOrDefault(session => session.IsOpen && session.PortName == port) ?? _workspace.Sessions.FirstOrDefault(session => session.IsOpen);
        ProfileCombo.ItemsSource = _configuration.Profiles;
        ProfileCombo.SelectedItem = _configuration.Profiles.FirstOrDefault(profile => profile.Id == _configuration.SelectedProfileId) ??
            (_configuration.Profiles.Count > 0 ? _configuration.Profiles[0] : null);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (PortCombo.SelectedItem is not SessionViewModel session || ProfileCombo.SelectedItem is not ProtocolDecoderProfile profile)
        { StatusText.Text = Resource("ProtocolDecoder.SelectSource"); return; }
        try { _decoder.Start(session, profile); StartButton.Content = Resource("Analysis.Restart"); }
        catch (Exception exception) { Program.DiagnosticLog?.Error("Protocol decoder start failed.", exception); StatusText.Text = exception.Message; }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { _displayPaused = !_displayPaused; PauseButton.Content = Resource(_displayPaused ? "Analysis.Resume" : "Analysis.Pause"); }
    private void Stop_Click(object sender, RoutedEventArgs e) => _decoder.Stop();
    private void Clear_Click(object sender, RoutedEventArgs e) { _decoder.Clear(); Frames.Clear(); FieldTree.Items.Clear(); HexText.Clear(); AsciiText.Clear(); DiagnosticText.Clear(); }
    private void Reload_Click(object sender, RoutedEventArgs e) { _configuration = ProtocolDecoderProfileStore.Load(); ReloadChoices(); }
    private void OpenConfig_Click(object sender, RoutedEventArgs e) { if (!File.Exists(ProtocolDecoderProfileStore.FilePath)) ProtocolDecoderProfileStore.ResetDefaults(); Process.Start(new ProcessStartInfo(ProtocolDecoderProfileStore.FilePath) { UseShellExecute = true }); }
    private void ResetConfig_Click(object sender, RoutedEventArgs e) { ProtocolDecoderProfileStore.ResetDefaults(); Reload_Click(sender, e); }

    private void OnTick(object? sender, EventArgs e)
    {
        ProtocolDecoderSnapshot snapshot = _decoder.Snapshot();
        if (!_displayPaused)
        {
            HashSet<long> current = Frames.Select(row => row.StoreSequence).ToHashSet();
            foreach (StoredProtocolFrame frame in snapshot.Frames.Frames) if (!current.Contains(frame.StoreSequence)) Frames.Add(new ProtocolFrameRow(frame));
            while (Frames.Count > 50_000) Frames.RemoveAt(0);
        }
        StatusText.Text = string.Format(CultureInfo.CurrentCulture, Resource("ProtocolDecoder.StatusFormat"), snapshot.State, snapshot.ReceivedBytes, Frames.Count, snapshot.Frames.EvictedCount);
        GapText.Text = snapshot.DroppedBlocks > 0 || snapshot.GapCount > 0
            ? string.Format(CultureInfo.CurrentCulture, Resource("ProtocolDecoder.GapFormat"), snapshot.GapCount, snapshot.DroppedBlocks, snapshot.DroppedBytes)
            : string.Empty;
    }

    private void FrameGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FieldTree.Items.Clear();
        if (FrameGrid.SelectedItem is not ProtocolFrameRow row) return;
        byte[] bytes = row.Frame.GetRawBytes();
        HexText.Text = string.Join(' ', bytes.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        AsciiText.Text = new string(bytes.Select(value => value is >= 32 and <= 126 ? (char)value : '.').ToArray());
        DiagnosticText.Text = string.Join(Environment.NewLine, row.Frame.Diagnostics.Select(item => $"[{item.Severity}] {item.Code}: {item.Message}"));
        foreach (ProtocolField field in row.Frame.Fields) FieldTree.Items.Add(CreateFieldItem(field));
    }
    private TreeViewItem CreateFieldItem(ProtocolField field)
    {
        TreeViewItem item = new() { Header = $"{field.Name}: {field.Value}{(string.IsNullOrEmpty(field.Unit) ? "" : " " + field.Unit)}", Tag = field };
        foreach (ProtocolField child in field.Children ?? []) item.Items.Add(CreateFieldItem(child));
        return item;
    }
    private void FieldTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: ProtocolField field }) return;
        int start = field.ByteOffset * 3;
        int length = Math.Max(0, field.ByteLength * 3 - 1);
        if (start >= 0 && start + length <= HexText.Text.Length) { HexText.Focus(); HexText.Select(start, length); }
        if (field.ByteOffset >= 0 && field.ByteOffset + field.ByteLength <= AsciiText.Text.Length) AsciiText.Select(field.ByteOffset, field.ByteLength);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new() { Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json|HEX (*.hex)|*.hex", FileName = "ducom-protocol-frames.csv" };
        if (dialog.ShowDialog(this) != true) return;
        ProtocolFrame[] frames = _decoder.Snapshot().Frames.Frames.Select(item => item.Frame).ToArray();
        await Task.Run(() => Export(dialog.FileName, dialog.FilterIndex, frames));
    }
    private static void Export(string path, int format, IReadOnlyList<ProtocolFrame> frames)
    {
        if (format == 2) { File.WriteAllText(path, JsonSerializer.Serialize(frames.Select(frame => new { frame.TimestampUtc, frame.Direction, frame.SourceGeneration, frame.Summary, frame.Integrity, RawHex = Convert.ToHexString(frame.GetRawBytes()) }), new JsonSerializerOptions { WriteIndented = true })); return; }
        if (format == 3) { File.WriteAllLines(path, frames.Select(frame => Convert.ToHexString(frame.GetRawBytes()))); return; }
        StringBuilder csv = new("TimestampUtc,Direction,SourceGeneration,Protocol,Type,Length,Integrity,Summary,RawHex\r\n");
        foreach (ProtocolFrame frame in frames) csv.Append(frame.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',').Append(frame.Direction).Append(',').Append(frame.SourceGeneration).Append(',').Append(Escape(frame.Summary.Protocol)).Append(',').Append(Escape(frame.Summary.Type)).Append(',').Append(frame.Length).Append(',').Append(frame.Integrity.Status).Append(',').Append(Escape(frame.Summary.Text)).Append(',').Append(Convert.ToHexString(frame.GetRawBytes())).Append("\r\n");
        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(false));
    }
    private static string Escape(string value) => value.Contains(',') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    private void ApplyPreferences(AnalysisWindowPreference value) { Width = value.Width; Height = value.Height; Topmost = value.Topmost; if (double.IsFinite(value.Left) && double.IsFinite(value.Top)) { Left = value.Left; Top = value.Top; WindowStartupLocation = WindowStartupLocation.Manual; } }
    private string Resource(string key) => TryFindResource(key) as string ?? key;
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _timer.Stop(); _timer.Tick -= OnTick; _decoder.Dispose();
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        AnalysisWindowPreferences all = _preferences.Load();
        _preferences.Save(all with { ProtocolDecoder = new AnalysisWindowPreference(bounds.Left, bounds.Top, bounds.Width, bounds.Height, Topmost,
            (PortCombo.SelectedItem as SessionViewModel)?.PortName, (ProfileCombo.SelectedItem as ProtocolDecoderProfile)?.Id) });
        GC.SuppressFinalize(this);
    }
}

public sealed record ProtocolFrameRow(long StoreSequence, ProtocolFrame Frame)
{
    public ProtocolFrameRow(StoredProtocolFrame stored) : this(stored.StoreSequence, stored.Frame) { }
    public DateTimeOffset Timestamp => Frame.TimestampUtc;
    public string Direction => Frame.Direction.ToString();
    public string Protocol => Frame.Summary.Protocol;
    public string Type => Frame.Summary.Type;
    public int Length => Frame.Length;
    public string Integrity => Frame.Integrity.Status.ToString();
    public string Summary => Frame.Summary.Text;
}
