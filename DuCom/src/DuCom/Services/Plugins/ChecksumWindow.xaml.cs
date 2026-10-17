using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DuCom.Plugin;
using Microsoft.Win32;

namespace DuCom.Services.Plugins;

public partial class ChecksumWindow : Window
{
    private readonly Func<string, Task<bool>> _load;
    private int _copyGeneration;
    private bool _animated;
    internal ChecksumWindowState ViewModel { get; } = new();

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public ChecksumWindow(Func<string, Task<bool>> load)
    {
        _load = load;
        InitializeComponent();
        DataContext = ViewModel;
        Topmost = PluginToolWindowPreferencesService.Load().Topmost;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Open, (_, _) => SelectFile(), (_, e) => e.CanExecute = ViewModel.CanOpen));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Open, Key.O, ModifierKeys.Control));
    }

    internal void UpdateState(IReadOnlyList<UiNode> nodes)
    {
        UiTextNode? state = nodes.OfType<UiTextNode>().FirstOrDefault(node => node.FieldId == "native-state");
        if (state is null) return;
        using JsonDocument document = JsonDocument.Parse(state.Text);
        ViewModel.Apply(document.RootElement);
    }

    internal async Task OpenFileAsync(string path)
    {
        if (!ViewModel.CanOpen) return;
        ViewModel.Begin(path);
        try
        {
            if (!await _load(path)) ViewModel.Fail("插件未能完成查询，请检查插件状态。");
        }
        catch (Exception exception) { ViewModel.Fail(exception.Message); }
        finally { ViewModel.Complete(); }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => SelectFile();
    private async void SelectFile()
    {
        if (!ViewModel.CanOpen) return;
        OpenFileDialog dialog = new() { Title = "打开固件", Filter = "固件文件 (*.bin;*.fw;*.ufw;*.prd;*.upd)|*.bin;*.fw;*.ufw;*.prd;*.upd|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) await OpenFileAsync(dialog.FileName);
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsLoaded) return;
        int generation = ++_copyGeneration;
        string code = ViewModel.FirmwareChecksum;
        try
        {
            Clipboard.SetText(code);
            ViewModel.CopyText = "已复制";
            ViewModel.Notify();
            await Task.Delay(1500);
            if (generation == _copyGeneration && ViewModel.FirmwareChecksum == code)
            {
                ViewModel.CopyText = "复制";
                ViewModel.Notify();
            }
        }
        catch (Exception exception) { ViewModel.Fail("复制失败 · " + exception.Message); }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool accepted = ViewModel.CanOpen && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && System.IO.File.Exists(paths[0]);
        ViewModel.IsDragging = accepted;
        ViewModel.Notify();
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (point.X <= 0 || point.Y <= 0 || point.X >= ActualWidth || point.Y >= ActualHeight)
        {
            ViewModel.IsDragging = false;
            ViewModel.Notify();
        }
    }
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        ViewModel.IsDragging = false;
        ViewModel.Notify();
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) await OpenFileAsync(paths[0]);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int corner = 2;
            int result = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref corner, sizeof(int));
            if (result < 0) System.Diagnostics.Debug.WriteLine($"DWM corner preference failed: {result:X8}");
        }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            int dark = 1;
            int result = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
            if (result < 0) System.Diagnostics.Debug.WriteLine($"DWM dark preference failed: {result:X8}");
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_animated || !SystemParameters.ClientAreaAnimation) return;
        _animated = true;
        var duration = TimeSpan.FromMilliseconds(900);
        DoubleAnimation opacity = new(1, 0.6, duration) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), FillBehavior = FillBehavior.Stop, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        DoubleAnimation scale = new(1, 1.08, duration) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(2), FillBehavior = FillBehavior.Stop, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        TitleIcon.BeginAnimation(OpacityProperty, opacity);
        TitleIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        TitleIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());
    }

    protected override void OnClosed(EventArgs e)
    {
        PluginToolWindowPreferencesService.Save(new PluginToolWindowPreferences(Topmost));
        base.OnClosed(e);
    }
}

internal sealed class ChecksumWindowState : INotifyPropertyChanged
{
    private bool _busy, _verified, _legacy, _error;
    public string FirmwareChecksum { get; private set; } = "—";
    public string DeviceName { get; private set; } = "—";
    public string ChipModel { get; private set; } = "—";
    public string Manufacturer { get; private set; } = "—";
    public string FileType { get; private set; } = "—";
    public string LoadedAt { get; private set; } = "—";
    public string? FilePath { get; private set; }
    public bool IsLoaded { get; private set; }
    public bool CanOpen => !_busy;
    public bool IsDragging { get; set; }
    public string CopyText { get; set; } = "复制";
    public string StatusMessage { get; private set; } = "拖入固件文件即可校验 · Ctrl+O 打开";
    public string StatusColor => _error ? "#F87171" : "#71717A";
    public string StatusDotColor => _error ? "#EF4444" : _busy ? "#64748B" : _verified ? "#10B981" : _legacy ? "#F59E0B" : "#64748B";
    public string ChecksumColor => _error ? "#F87171" : _busy ? "#A1A1AA" : _verified ? "#22D3EE" : _legacy ? "#F59E0B" : "#A1A1AA";
    public double ChecksumGlowOpacity => _verified && !_busy && !_error ? 0.4 : 0;
    public double StatusGlowOpacity => _verified && !_busy && !_error ? 0.8 : 0;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Begin(string path)
    {
        _busy = true;
        _verified = _legacy = _error = IsLoaded = IsDragging = false;
        FilePath = path;
        FirmwareChecksum = DeviceName = ChipModel = Manufacturer = LoadedAt = FileType = "—";
        CopyText = "复制";
        StatusMessage = "正在读取固件…";
        Notify();
    }
    public void Complete() { _busy = false; Notify(); }
    public void Fail(string message) { _error = true; StatusMessage = "加载失败 · " + message; Notify(); }
    public void Apply(JsonElement state)
    {
        string? Text(string key) => state.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool Flag(string key) => state.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
        if (Flag("Busy")) return;
        if (Flag("Error")) { Fail(Text("Status") ?? "固件校验不一致，请确认文件是否完整。"); return; }
        if (Text("Path") is not { } path) return;
        FilePath = path;
        FirmwareChecksum = Text("Code") is { Length: > 0 } code ? code : "未嵌入";
        DeviceName = Text("Name") ?? "未识别";
        string chip = Text("Chip") ?? "未识别";
        ChipModel = chip.StartsWith("best", StringComparison.OrdinalIgnoreCase) ? chip[4..].ToUpperInvariant() : chip.ToUpperInvariant();
        Manufacturer = Text("Manufacturer") ?? "未知厂商";
        LoadedAt = Text("Date") ?? "未嵌入";
        FileType = Text("FileType") ?? "未识别";
        IsLoaded = Text("Code") is { Length: > 0 };
        _verified = Flag("Verified");
        _legacy = !_verified;
        _error = false;
        _busy = false;
        string status = _verified ? "已校验" : Manufacturer is "蓝讯" or "杰理" ? "已读取官方校验码" : "已读取 · 旧版或未嵌入校验";
        StatusMessage = status + " · " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Notify();
    }
}
