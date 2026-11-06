using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using DuCom.Plugin;
using DuCom.Plugins.Crc;

namespace DuCom.Services.Plugins;

public partial class CrcWindow : Window
{
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<bool>> _invoke;
    private bool _ready, _applying, _busy, _ascii;
    private int _revision, _submittedRevision;

    public CrcWindow(Func<string, IReadOnlyDictionary<string, string>, Task<bool>> invoke)
    {
        _invoke = invoke;
        InitializeComponent();
        PluginVisualTheme.Apply(this);
        Topmost = PluginToolWindowPreferencesService.Load().Topmost;
        ModelSelect.ItemsSource = new[] { new CrcModel("自定义", 16, 0x8005, 0xFFFF, 0, true, true) }.Concat(CrcCalculator.Models);
        ModelTable.ItemsSource = CrcCalculator.Models;
        WidthInput.ItemsSource = Enumerable.Range(1, 32);
        HelpText.Text = "CRC（Cyclic Redundancy Check，循环冗余校验）用于检测数据传输错误。发送方按生成多项式计算校验值，接收方以相同参数重新计算并比较，以检查数据完整性。\n\n"
            + "NAME：参数模型名称。\nWIDTH：CRC 的位数，支持 1–32 位。\nPOLY：十六进制生成多项式，省略最高位的 1。例如 CRC-32 的 04C11DB7 对应完整多项式 104C11DB7。\nINIT：计算开始时寄存器的十六进制初始值。\nREFIN：输入的每个字节是否按位反转。\nREFOUT：计算结束后、异或输出前，是否将 WIDTH 位寄存器按位反转。\nXOROUT：寄存器与此十六进制数异或后得到最终 CRC。\n\n"
            + "Hex 和 ASCII 切换会转换当前数据；ASCII 仅支持 0–127，空格和换行也参与计算。复制时请留意首尾不可见字符。文件校验直接使用文件原始字节，支持空文件及最大 64 MiB 文件。\nHex 按宽度补齐十六进制位数，Bin 按 WIDTH 补齐。REFOUT 的位反转与协议中的高低字节顺序是两件不同的事。\n示例：ASCII 123456789 / CRC-16/MODBUS → 4B37。";
        _ready = true;
        ModelSelect.SelectedItem = CrcCalculator.Models.Single(model => model.Name == "CRC-16/MODBUS");
        Status("输入数据或打开文件，选择模型后计算。");
        InputBindings.Add(new KeyBinding(new CalculateCommand(() => RunAsync("calculate")), Key.Enter, ModifierKeys.Control));
    }

    internal void UpdateState(IReadOnlyList<UiNode> nodes)
    {
        UiTextNode? state = nodes.OfType<UiTextNode>().FirstOrDefault(node => node.FieldId == "native-state");
        if (state is null) return;
        using JsonDocument document = JsonDocument.Parse(state.Text);
        JsonElement root = document.RootElement;
        bool busy = root.GetProperty("Busy").GetBoolean();
        SetBusy(busy);
        if (_submittedRevision != _revision) return;
        string hex = root.GetProperty("Hex").GetString() ?? "";
        HexResult.Text = hex.Length == 0 ? "—" : hex;
        BinResult.Text = root.GetProperty("Bin").GetString() is { Length: > 0 } bin ? bin : "—";
        CopyHexButton.IsEnabled = CopyBinButton.IsEnabled = hex.Length != 0 && !busy;
        Status(root.GetProperty("Status").GetString() ?? "", root.GetProperty("Error").GetBoolean());
    }

    private Dictionary<string, string> Values() => new()
    {
        ["data"] = DataInput.Text, ["mode"] = _ascii ? "ascii" : "hex", ["width"] = WidthInput.SelectedItem?.ToString() ?? "",
        ["poly"] = PolyInput.Text, ["init"] = InitInput.Text, ["xor"] = XorInput.Text,
        ["refin"] = (RefInInput.IsChecked == true).ToString(), ["refout"] = (RefOutInput.IsChecked == true).ToString(),
    };

    private void Invalidate()
    {
        if (!_ready || _applying) return;
        _revision++;
        HexResult.Text = BinResult.Text = "—";
        CopyHexButton.IsEnabled = CopyBinButton.IsEnabled = false;
        Status("数据或参数已更新，请重新计算。");
    }
    private void Input_Changed(object sender, RoutedEventArgs e) => Invalidate();
    private void Parameter_Changed(object sender, SelectionChangedEventArgs e) => Invalidate();
    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ModelSelect.SelectedItem is not CrcModel model) return;
        bool custom = model.Name == "自定义";
        ParameterFields.IsEnabled = RefInInput.IsEnabled = RefOutInput.IsEnabled = custom;
        if (!custom)
        {
            _applying = true;
            WidthInput.SelectedItem = model.Width;
            PolyInput.Text = model.PolyHex; InitInput.Text = model.InitHex; XorInput.Text = model.XorHex;
            RefInInput.IsChecked = model.RefIn; RefOutInput.IsChecked = model.RefOut;
            _applying = false;
        }
        Invalidate();
    }
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool ascii = AsciiMode.IsChecked == true;
        if (ascii == _ascii) return;
        try
        {
            byte[] bytes = CrcCalculator.ParseInput(DataInput.Text, _ascii);
            if (ascii && bytes.Any(value => value > 127)) throw new FormatException("当前数据含非 ASCII 字节，请继续使用 Hex。");
            DataInput.Text = ascii ? Encoding.ASCII.GetString(bytes) : Convert.ToHexString(bytes);
            _ascii = ascii;
            InputHint.Text = ascii ? "ASCII：例如 1234；空格和换行均参与计算。" : "Hex：31 32 33 34，也支持连续字节和 0x 前缀。";
            Invalidate();
        }
        catch (FormatException exception)
        {
            HexMode.IsChecked = !_ascii; AsciiMode.IsChecked = _ascii;
            Status(exception.Message, true);
        }
    }
    private async Task RunAsync(string command)
    {
        if (_busy) return;
        _submittedRevision = _revision;
        HexResult.Text = BinResult.Text = "—";
        CopyHexButton.IsEnabled = CopyBinButton.IsEnabled = false;
        SetBusy(true);
        Status(command == "file" ? "请选择文件…" : "正在计算…");
        try { if (!await _invoke(command, Values())) Status("插件未能完成计算，请检查插件状态。", true); }
        catch (Exception exception) { Status("计算失败：" + exception.Message, true); }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool busy) { _busy = busy; CalculateButton.IsEnabled = FileButton.IsEnabled = !busy; }
    private async void Calculate_Click(object sender, RoutedEventArgs e) => await RunAsync("calculate");
    private async void File_Click(object sender, RoutedEventArgs e) => await RunAsync("file");
    private void Example_Click(object sender, RoutedEventArgs e) { HexMode.IsChecked = true; DataInput.Text = "31 32 33 34 35 36 37 38 39"; }
    private void Clear_Click(object sender, RoutedEventArgs e) { DataInput.Clear(); Invalidate(); Status("已清空数据与结果。"); }
    private void CopyHex_Click(object sender, RoutedEventArgs e) => Copy(HexResult.Text);
    private void CopyBin_Click(object sender, RoutedEventArgs e) => Copy(BinResult.Text);
    private void Copy(string text)
    {
        try { Clipboard.SetText(text); Status("已复制：" + text); }
        catch (Exception exception) { Status("复制失败：" + exception.Message, true); }
    }
    private void Status(string text, bool error = false) { StatusText.Text = text; StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Brush.Danger" : "Brush.TextSecondary"); }
    private void ModelTable_DoubleClick(object sender, MouseButtonEventArgs e) { if (ModelTable.SelectedItem is CrcModel model) ModelSelect.SelectedItem = model; }
    private void Reference_Navigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception exception) { Status("打开链接失败：" + exception.Message, true); }
        e.Handled = true;
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeButton is null) return;
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "还原" : "最大化");
        MaximizeIcon.Data = Geometry.Parse(maximized ? "M 2,0 L 10,0 L 10,8 M 0,2 L 8,2 L 8,10 L 0,10 Z" : "M 0,0 L 10,0 L 10,10 L 0,10 Z");
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosed(EventArgs e) { PluginToolWindowPreferencesService.Save(new PluginToolWindowPreferences(Topmost)); base.OnClosed(e); }
    private sealed class CalculateCommand(Func<Task> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter) => await run();
    }
}
