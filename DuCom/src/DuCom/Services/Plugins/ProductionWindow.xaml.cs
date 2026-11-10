using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Input;
using DuCom.Plugin;
using DuCom.Plugins.Production;
using DuCom.ViewModels;

namespace DuCom.Services.Plugins;

public partial class ProductionWindow : Window
{
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<bool>> _invoke;
    private readonly Func<IEnumerable<PortItemViewModel>>? _portsProvider;
    private ProductionProfile _profile = ProductionProfile.CreateExample();
    private bool _running, _dispatching, _left, _right, _initialized, _selecting, _approved, _closing;
    private readonly Dictionary<string, string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private string _recordPath = "";
    public ObservableCollection<ProductionResultRow> Rows { get; } = [];
    private static string ProfilesDirectory => Path.Combine(AppContext.BaseDirectory, "profiles");
    public ProductionWindow(Func<string, IReadOnlyDictionary<string, string>, Task<bool>> invoke, Func<IEnumerable<PortItemViewModel>>? portsProvider = null)
    {
        _invoke = invoke; _portsProvider = portsProvider; InitializeComponent(); PluginVisualTheme.Apply(this); DataContext = this; RefreshPorts(); Closing += OnClosing;
        LeftPort.PreviewMouseLeftButtonDown += PreparePortDropdown; RightPort.PreviewMouseLeftButtonDown += PreparePortDropdown;
        ResultsGrid.PreviewMouseWheel += (_, _) => { if (_running) FollowExecution.IsChecked = false; };
    }
    internal void UpdateState(IReadOnlyList<UiNode> nodes)
    {
        string json = string.Concat(nodes.OfType<UiTextNode>().Where(n => n.FieldId.StartsWith("native-state-", StringComparison.Ordinal)).Select(n => n.Text));
        if (json.Length == 0) return;
        try
        {
            using JsonDocument d = JsonDocument.Parse(json); JsonElement state = d.RootElement;
            ProductionProfile profile = state.GetProperty("Profile").Deserialize<ProductionProfile>(ProductionProfile.JsonOptions)!;
            if (!_initialized || profile.ToJson() != _profile.ToJson()) { _profile = profile; Rebuild(); RefreshProfiles(); _initialized = true; }
            _running = state.GetProperty("Running").GetBoolean(); _left = state.GetProperty("LeftConnected").GetBoolean(); _right = state.GetProperty("RightConnected").GetBoolean();
            LeftState.Text = _left ? "已连接" : "未连接"; RightState.Text = _right ? "已连接" : "未连接";
            LeftState.SetResourceReference(TextBlock.ForegroundProperty, _left ? "Brush.Success" : "Brush.TextSecondary"); RightState.SetResourceReference(TextBlock.ForegroundProperty, _right ? "Brush.Success" : "Brush.TextSecondary");
            LeftConnect.Content = _left ? "断开" : "连接"; RightConnect.Content = _right ? "断开" : "连接";
            StatusText.Text = state.GetProperty("Status").GetString(); StatusText.ToolTip = state.TryGetProperty("RecordPath", out var path) ? path.GetString() : StatusText.Text;
            _recordPath = state.TryGetProperty("RecordPath", out var record) ? record.GetString() ?? "" : _recordPath;
            try { ExportRecordsButton.IsEnabled = _recordPath.Length > 0 && File.Exists(_recordPath + ".csv"); }
            catch (Exception) { ExportRecordsButton.IsEnabled = false; }
            foreach (var row in Rows)
            {
                row.Left = Result(state, "LeftResults", row.Command.Id); row.Right = Result(state, "RightResults", row.Command.Id); row.Refresh();
            }
            SetSignal(LeftSignal, LeftSignalBox, "左耳", state.TryGetProperty("LeftState", out var ls) ? ls.GetString() ?? "待执行" : "待执行");
            SetSignal(RightSignal, RightSignalBox, "右耳", state.TryGetProperty("RightState", out var rs) ? rs.GetString() ?? "待执行" : "待执行");
            if (FollowExecution.IsChecked == true && state.TryGetProperty("ActiveCommand", out var active))
            {
                var current = Rows.FirstOrDefault(r => r.Command.Id == active.GetString());
                if (current is not null) ResultsGrid.ScrollIntoView(current);
            }
            int passed = state.TryGetProperty("Passed", out var p) ? p.GetInt32() : 0, failed = state.TryGetProperty("Failed", out var f) ? f.GetInt32() : 0;
            StatisticsText.Text = $"总数 {passed + failed}          通过 {passed}          不通过 {failed}          通过率 {(passed + failed == 0 ? "—" : (100.0 * passed / (passed + failed)).ToString("0.0") + "%")}";
            Enabled();
        }
        catch (Exception e) { StatusText.Text = e.Message; }
    }
    private static string Result(JsonElement state, string key, string id) => state.TryGetProperty(key, out var results) && results.TryGetProperty(id, out var value) ? value.GetString() ?? "—" : "—";
    private void Rebuild() { Rows.Clear(); foreach (var c in _profile.Commands.Where(c => c.Enabled)) Rows.Add(new(c)); }
    private void Enabled()
    {
        ProjectBar.IsEnabled = Connections.IsEnabled = !_running && !_dispatching;
        LeftPort.IsEnabled = !_left; RightPort.IsEnabled = !_right;
        StartButton.IsEnabled = !_running && !_dispatching; StopButton.IsEnabled = _running && !_dispatching; LoopSelected.IsEnabled = !_running && !_dispatching;
        ClearBatchButton.IsEnabled = !_running && !_dispatching;
        ClearRecordsButton.IsEnabled = !_running && !_dispatching;
        ContinuousButton.IsEnabled = !_running && !_dispatching;
    }
    private async Task Invoke(string command, Dictionary<string, string>? values = null)
    {
        if (_dispatching) return; _dispatching = true; Enabled();
        try { if (!await _invoke(command, values ?? [])) StatusText.Text = "插件不可用"; }
        catch (Exception e) { StatusText.Text = e.Message; }
        finally { _dispatching = false; Enabled(); }
    }
    private void RefreshPorts()
    {
        string? l = LeftPort.SelectedItem as string, r = RightPort.SelectedItem as string;
        string[] ports = SelectPhysicalPorts(_portsProvider);
        if (LeftPort.ItemsSource is string[] existing && existing.SequenceEqual(ports)) return;
        LeftPort.ItemsSource = ports; RightPort.ItemsSource = ports;
        LeftPort.SelectedItem = ports.Contains(l) ? l : ports.Contains("COM42") ? "COM42" : ports.FirstOrDefault();
        RightPort.SelectedItem = ports.Contains(r) ? r : ports.Contains("COM40") ? "COM40" : ports.Skip(1).FirstOrDefault();
    }

    /// <summary>
    /// Physical adapters only: reuse the host's classified port list and drop virtual
    /// devices (VAR) plus entries that have not been identified yet — the same rule the
    /// plugin-facing host API applies. Pure so the filter can be unit-tested without
    /// constructing the heavy production window on the shared test STA thread.
    /// </summary>
    internal static string[] SelectPhysicalPorts(Func<IEnumerable<ViewModels.PortItemViewModel>>? portsProvider) =>
        portsProvider is null ? [.. SerialPort.GetPortNames().Order()]
        : [.. portsProvider()
            .Where(port => !string.Equals(port.PortType, "VAR", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(port.DeviceInstanceId))
            .Select(port => port.PortName).Distinct().Order(StringComparer.OrdinalIgnoreCase)];
    private void RefreshProfiles()
    {
        _selecting = true;
        try
        {
            _paths.Clear();
            if (Directory.Exists(ProfilesDirectory)) foreach (string file in Directory.EnumerateFiles(ProfilesDirectory, "*.json").Where(f => !Path.GetFileName(f).Equals("X8I-pairing.json", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals("X8I.json", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Equals("X8I_V1.json", StringComparison.OrdinalIgnoreCase)))
                try { var p = ProductionProfile.FromJson(File.ReadAllText(file)); _paths[p.Name] = file; } catch (Exception e) { Program.DiagnosticLog?.Warning(e.Message); }
            // X8I_V1.json is excluded above: it is the legacy loose copy of the template
            // that now ships embedded. Ignoring it lets the embedded (current) template
            // win; user-saved copies live under project-*.json and are unaffected.
            // The bundled template ships inside the exe; offer it even with an empty
            // profiles folder. A user-saved copy with the same name wins over it.
            _paths.TryAdd(ProductionBundledTemplate.Name, "");
            Profiles.ItemsSource = _paths.Keys.Append(_profile.Name).Distinct().Order().ToArray(); Profiles.SelectedItem = _profile.Name;
        }
        finally { _selecting = false; }
    }
    private async void Profiles_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_selecting || Profiles.SelectedItem is not string name || name == _profile.Name) return;
        if (_left || _right) { StatusText.Text = "切换项目请先断开串口"; RefreshProfiles(); return; }
        try
        {
            string json = name == ProductionBundledTemplate.Name ? ProductionBundledTemplate.LoadJson()
                : File.ReadAllText(_paths[name]);
            await Apply(ProductionProfile.FromJson(json));
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async Task Apply(ProductionProfile profile)
    {
        profile.Validate(); Directory.CreateDirectory(ProfilesDirectory);
        string safe = string.Concat(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string path = Path.Combine(ProfilesDirectory, "project-" + safe + ".json");
        File.WriteAllText(path + ".tmp", profile.ToJson()); File.Move(path + ".tmp", path, true);
        _profile = profile; Rebuild(); RefreshProfiles(); await Invoke("apply", new() { ["profile"] = profile.ToJson() });
    }
    private async void Configure_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ProductionProfileEditor(ProductionProfile.FromJson(_profile.ToJson())) { Owner = this };
        if (editor.ShowDialog() == true) try { await Apply(editor.Profile); } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (_left || _right) { StatusText.Text = "新建项目前请先断开串口"; return; }
        var editor = new ProductionProfileEditor(new() { Name = "新项目", Protocol = new(), Commands = [] }) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            if (_paths.ContainsKey(editor.Profile.Name) || editor.Profile.Name == _profile.Name) { StatusText.Text = "项目名称已存在，请使用不同名称"; return; }
            try { await Apply(editor.Profile); } catch (Exception ex) { StatusText.Text = ex.Message; }
        }
    }
    private async void ImportProject_Click(object sender, RoutedEventArgs e)
    {
        if (_left || _right) { StatusText.Text = "导入项目前请先断开串口"; return; }
        Microsoft.Win32.OpenFileDialog dialog = new() { Filter = "项目配置 (*.json)|*.json", CheckFileExists = true, Title = "导入项目配置" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ProductionProfile profile = ProductionProfile.FromJson(File.ReadAllText(dialog.FileName));
            if (profile.Commands.Count == 0) { StatusText.Text = "导入的项目没有启用任何命令，请先在项目配置中添加"; return; }
            // Re-importing an existing project name intentionally replaces its saved file.
            await Apply(profile);
            StatusText.Text = $"已导入项目 {profile.Name}";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void ExportTemplate_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Filter = "项目配置 (*.json)|*.json",
            FileName = "产测项目模版.json",
            Title = "导出公共项目模版",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // The public template is vendor-neutral: generic placeholders plus in-Notes
            // documentation, so customers adapt it to their own protocol from scratch.
            string json = ProductionBundledTemplate.LoadGenericJson();
            ProductionProfile.FromJson(json); // Refuse to hand out a template that would not import.
            File.WriteAllText(dialog.FileName, json, System.Text.Encoding.UTF8);
            StatusText.Text = $"已导出通用模版 {Path.GetFileName(dialog.FileName)}（含协议与命令说明，导入后按贵司协议修改）";
        }
        catch (Exception ex) { StatusText.Text = "导出失败 · " + ex.Message; }
    }
    private void ExportRecords_Click(object sender, RoutedEventArgs e)
    {
        if (_recordPath.Length == 0) { StatusText.Text = "还没有产测记录，先完成一轮测试"; return; }
        // One export covers EVERY record of this project, not just the current file:
        // collect its Production-*.csv across the plugin's dated log folders (oldest
        // first — the file names start with a timestamp), then merge into one sheet.
        string? recordDir = Path.GetDirectoryName(_recordPath);
        string pluginRoot = recordDir is not null ? Directory.GetParent(recordDir)?.FullName ?? recordDir : "";
        if (!pluginRoot.EndsWith("com.ducom.production", StringComparison.OrdinalIgnoreCase)) pluginRoot = recordDir ?? "";
        string filePrefix = "Production-" + string.Concat(_profile.Name.Take(80).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + "-";
        List<string> recordFiles = [];
        if (pluginRoot.Length > 0 && Directory.Exists(pluginRoot))
            recordFiles.AddRange(Directory.EnumerateFiles(pluginRoot, "Production-*.csv", SearchOption.AllDirectories)
                .Where(file => Path.GetFileName(file).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase));
        if (recordFiles.Count == 0 || !File.Exists(_recordPath + ".csv")) { StatusText.Text = "还没有产测记录，先完成一轮测试"; return; }
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Filter = "Excel 表 (*.xlsx)|*.xlsx",
            FileName = Path.GetFileName(_recordPath) + ".xlsx",
            Title = "导出产测记录",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Column order follows the current profile's enabled commands, so the sheet
            // matches the test items the operator sees on this page.
            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
            ProductionRecordExporter.Export(recordFiles, dialog.FileName, [.. Rows.Select(row => row.Name)],
                new ProductionExportContext(_profile.Name, "DuCom V" + version));
            StatusText.Text = $"已导出产测记录 {Path.GetFileName(dialog.FileName)}（{recordFiles.Count} 个记录文件 · 每次测试一行）";
        }
        catch (Exception ex) { StatusText.Text = "导出失败 · " + ex.Message; }
    }
    private async void ClearRecords_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _dispatching) return;
        if (!ThemedMessageDialog.Confirm(this,
                $"删除项目“{_profile.Name}”的全部产测记录文件并从零重新开始记录？\n删除后无法恢复，需要留档请先导出。",
                "清空记录")) return;
        await Invoke("clear-records");
    }
    private async void DeleteProject_Click(object sender, RoutedEventArgs e)    {
        if (_running || _dispatching) return;
        if (_left || _right) { StatusText.Text = "删除项目前请先断开串口"; return; }
        string name = _profile.Name;
        if (!ThemedMessageDialog.Confirm(this, $"删除项目“{name}”？\n测试日志和 CSV 会保留。", "删除项目")) return;
        try
        {
            List<string> files = [];
            if (Directory.Exists(ProfilesDirectory))
                foreach (string file in Directory.EnumerateFiles(ProfilesDirectory, "*.json"))
                    try { if (ProductionProfile.FromJson(File.ReadAllText(file)).Name == name) files.Add(file); }
                    catch (Exception ex) { Program.DiagnosticLog?.Warning(ex.Message); }
            foreach (string file in files) File.Delete(file);
            _paths.Remove(name);
            ProductionProfile next = new() { Name = "未选择项目", Commands = [] };
            foreach (string file in _paths.Values)
                if (File.Exists(file)) { next = ProductionProfile.FromJson(File.ReadAllText(file)); break; }
            _profile = next; Rebuild(); RefreshProfiles();
            await Invoke("apply", new() { ["profile"] = next.ToJson() });
            StatusText.Text = "项目已删除，测试文件保留";
        }
        catch (Exception ex) { StatusText.Text = "删除失败 · " + ex.Message; }
    }
    private async void LeftConnect_Click(object sender, RoutedEventArgs e) => await Invoke("connect-left", new() { ["port"] = LeftPort.SelectedItem as string ?? "" });
    private async void RightConnect_Click(object sender, RoutedEventArgs e) => await Invoke("connect-right", new() { ["port"] = RightPort.SelectedItem as string ?? "" });
    private async void Start_Click(object sender, RoutedEventArgs e)
        => await StartModeAsync(LoopSelected.IsChecked == true ? "loop" : "once");
    private async void Continuous_Click(object sender, RoutedEventArgs e) => await StartModeAsync("continuous");
    private async Task StartModeAsync(string mode)
    {
        List<string> channels = []; if (LeftSelected.IsChecked == true) channels.Add("0"); if (RightSelected.IsChecked == true) channels.Add("1");
        if (channels.Count == 0) { StatusText.Text = "请勾选测试通道"; return; }
        if ((channels.Contains("0") && !_left) || (channels.Contains("1") && !_right)) { StatusText.Text = "请连接已勾选的串口"; return; }
        foreach (var row in Rows) { row.Left = row.Right = "—"; row.Refresh(); }
        SetSignal(LeftSignal, LeftSignalBox, "左耳", channels.Contains("0") ? "执行中" : "未参与");
        SetSignal(RightSignal, RightSignalBox, "右耳", channels.Contains("1") ? "执行中" : "未参与");
        await Invoke(mode, new() { ["profile"] = _profile.ToJson(), ["channels"] = string.Join(",", channels) });
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await Invoke("stop");
    private async void ClearBatch_Click(object sender, RoutedEventArgs e) => await Invoke("clear-batch");
    private void PreparePortDropdown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBox { IsDropDownOpen: false }) RefreshPorts();
    }
    private static void SetSignal(TextBlock text, Border box, string side, string state)
    {
        bool failed = state.Contains("不通过", StringComparison.Ordinal) || state.Contains("异常", StringComparison.Ordinal);
        bool passed = state == "执行完成 · 通过";
        text.Text = side + " · " + state;
        text.SetResourceReference(TextBlock.ForegroundProperty, failed ? "Brush.Danger" : passed ? "Brush.Success" : state == "执行中" ? "Brush.Accent" : "Brush.TextSecondary");
        box.SetResourceReference(Border.BackgroundProperty, failed ? "Brush.DangerSoft" : passed ? "Brush.SuccessSoft" : state == "执行中" ? "Brush.AccentSoft" : "Brush.PanelSurface");
    }
    private void Result_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid { DataContext: ProductionResultRow row, Tag: string side }) return;
        string result = side == "left" ? row.Left : row.Right;
        Window detail = new() { Title = row.Name + " · " + (side == "left" ? "左耳" : "右耳"), Owner = this, Width = 620, Height = 320, MinWidth = 400, MinHeight = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(9, 9, 11)), Foreground = Brushes.WhiteSmoke };
        detail.Resources.MergedDictionaries.Add(new() { Source = new Uri("/DuCom;component/Services/Plugins/ProductionStyles.xaml", UriKind.Relative) });
        detail.Content = new TextBox { Text = result, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        PluginVisualTheme.Apply(detail);
        detail.ShowDialog();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    public void CloseFromHost() { _approved = true; Close(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_approved) return; e.Cancel = true; if (_closing) return; _closing = true;
        try { await _invoke("close", new Dictionary<string, string>()); } catch (Exception ex) { Program.DiagnosticLog?.Warning(ex.Message); } finally { _approved = true; Close(); }
    }
}

public sealed class ProductionResultRow(ProductionCommand command) : INotifyPropertyChanged
{
    public ProductionCommand Command { get; } = command;
    public string Name => Command.Name;
    public string Left { get; set; } = "—";
    public string Right { get; set; } = "—";
    public string LeftShort => Left.Length > 40 ? Left[..40] + "…" : Left;
    public string RightShort => Right.Length > 40 ? Right[..40] + "…" : Right;
    private static readonly string[] FailureWords = ["失败", "错误", "超时", "异常", "未连接", "越界", "不足", "未打开"];
    private static bool Failed(string s) => FailureWords.Any(word => s.Contains(word, StringComparison.Ordinal));
    private static string Mark(string s) => s is "" or "—" or "已停止" || s.Contains("执行中", StringComparison.Ordinal) ? "" : Failed(s) ? "✕" : "✓";
    public string LeftMark => Mark(Left);
    public string RightMark => Mark(Right);
    private static Brush Color(string s) => Failed(s) ? Brushes.Salmon : Brushes.MediumAquamarine;
    public Brush LeftBrush => Color(Left);
    public Brush RightBrush => Color(Right);
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() { foreach (string name in new[] { nameof(Left), nameof(Right), nameof(LeftShort), nameof(RightShort), nameof(LeftBrush), nameof(RightBrush), nameof(LeftMark), nameof(RightMark) }) PropertyChanged?.Invoke(this, new(name)); }
}
