using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DuCom.Plugins.Production;

namespace DuCom.Services.Plugins;

public sealed class ProductionCommandEditor : Window
{
    private readonly ProductionCommand _command;
    private readonly ProtocolSettings _project;
    private readonly DataGrid _fields = new();
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, Height = 82, FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _txLength;
    private readonly TextBox _rxLength;
    public ProductionCommandEditor(ProductionCommand command, ProtocolSettings project, IReadOnlyList<ProductionCommand>? previousSteps = null)
    {
        _command = command; _project = project; command.Frame ??= ProductionProfileEditor.Clone(project);
        Title = "命令详情"; Width = 850; Height = 800; MinWidth = 780; MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(9, 9, 11)); Foreground = Brushes.WhiteSmoke;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/DuCom;component/Services/Plugins/ProductionStyles.xaml", UriKind.Relative) });
        Grid root = new() { Margin = new Thickness(24) }; root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        StackPanel body = new();
        WrapPanel common = new();
        ProductionProfileEditor.AddInput(common, "命令名称", command, "Name", 250);
        ProductionProfileEditor.AddInput(common, "发送后间隔 ms", command, "DelayMs", 110);
        ProductionProfileEditor.AddInput(common, "应答超时 ms", command, "TimeoutMs", 110); body.Children.Add(common);
        Grid frames = new(); frames.ColumnDefinitions.Add(new()); frames.ColumnDefinitions.Add(new() { Width = new GridLength(20) }); frames.ColumnDefinitions.Add(new());
        StackPanel tx = new(), rx = new(); frames.Children.Add(tx); Grid.SetColumn(rx, 2); frames.Children.Add(rx);
        tx.Children.Add(new TextBlock { Text = "发送", FontSize = 15, Margin = new Thickness(0, 8, 0, 12) });
        rx.Children.Add(new TextBlock { Text = "回复", FontSize = 15, Margin = new Thickness(0, 8, 0, 12) });
        ProductionProfileEditor.AddInput(tx, "头 HEX", command.Frame, "TxHeader", 300);
        ProductionProfileEditor.AddInput(rx, "头 HEX", command.Frame, "RxHeader", 300);
        _txLength = LengthBox(tx, "长度（留空自动）", command.TxLength);
        _rxLength = LengthBox(rx, "长度（留空自动）", command.RxLength);
        ProductionProfileEditor.AddInput(tx, "CMD HEX", command, "Hex", 300);
        ProductionProfileEditor.AddInput(rx, "CMD HEX", command, "ResponseMatch", 300);
        foreach (StackPanel panel in new[] { tx, rx })
        {
            panel.Children.Add(new TextBlock { Text = "CRC", Margin = new Thickness(0, 0, 0, 6) });
            ComboBox crc = new() { ItemsSource = Enum.GetValues<ChecksumKind>(), Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            crc.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding("Checksum") { Source = command.Frame, Mode = BindingMode.TwoWay }); panel.Children.Add(crc);
        }
        body.Children.Add(frames);
        WrapPanel fieldTools = new() { Margin = new Thickness(0, 12, 0, 8) };
        fieldTools.Children.Add(new TextBlock { Text = "回复数据 · 按协议顺序添加", Margin = new Thickness(0, 0, 12, 0) });
        fieldTools.Children.Add(ProductionProfileEditor.ActionButton("添加字段", () => { Commit(); command.Fields.Add(new() { Name = "MAC", Type = FieldType.Mac }); Refresh(); }));
        fieldTools.Children.Add(ProductionProfileEditor.ActionButton("删除字段", () => { if (_fields.SelectedItem is ResponseField f) { Commit(); command.Fields.Remove(f); Refresh(); } }));
        body.Children.Add(fieldTools);
        _fields.Height = 150;
        _fields.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new Binding("Name"), Width = 105 });
        _fields.Columns.Add(new DataGridComboBoxColumn { Header = "类型", ItemsSource = Enum.GetValues<FieldType>(), SelectedItemBinding = new Binding("Type"), Width = 100 });
        _fields.Columns.Add(new DataGridTextColumn { Header = "长度", Binding = new Binding("Length"), Width = 55 });
        _fields.Columns.Add(new DataGridTextColumn { Header = "单位", Binding = new Binding("Unit"), Width = 55 });
        _fields.Columns.Add(new DataGridCheckBoxColumn { Header = "小端", Binding = new Binding("LittleEndian"), Width = 50 });
        _fields.Columns.Add(new DataGridTextColumn { Header = "示例 HEX（仅计算用）", Binding = new Binding("Example"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        Refresh(); body.Children.Add(_fields);
        body.Children.Add(new TextBlock { Text = "MAC 固定 6 字节；整数长度由类型决定。多个 MAC 添加多行。示例为空按 00 计算。", Foreground = Brushes.LightGray, Margin = new Thickness(0, 8, 0, 12), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(ProductionProfileEditor.ActionButton("计算报文（不发送）", Calculate)); body.Children.Add(_preview);
        CheckBox wait = new() { Content = "等待回复", Margin = new Thickness(0, 12, 0, 0) }; wait.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("WaitResponse") { Source = command, Mode = BindingMode.TwoWay }); body.Children.Add(wait);
        if (previousSteps is not null)
        {
            StackPanel transfer = new() { Margin = new Thickness(0, 12, 0, 0) };
            ComboBox route = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, DisplayMemberPath = "Label", SelectedValuePath = "Key",
                ItemsSource = new[] { new { Key = "selected", Label = "跟随主页所选通道" }, new { Key = "left", Label = "固定左耳（双耳协作）" }, new { Key = "right", Label = "固定右耳（双耳协作）" } } };
            route.SelectedValue = command.PairingStep ? command.Channel == EarChannel.Right ? "right" : "left" : "selected";
            route.SelectionChanged += (_, _) => { command.PairingStep = (string?)route.SelectedValue != "selected"; command.Channel = (string?)route.SelectedValue == "right" ? EarChannel.Right : command.PairingStep ? EarChannel.Left : EarChannel.Both; };
            transfer.Children.Add(new TextBlock { Text = "执行位置", Margin = new Thickness(0, 0, 0, 6) }); transfer.Children.Add(route);
            ComboBox source = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Left, DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new Thickness(0, 6, 0, 6),
                ItemsSource = new[] { new { Id = "", Name = "不追加来源数据" } }.Concat(previousSteps.Select(s => new { s.Id, s.Name })).ToArray() };
            source.SelectedValue = command.ResponseSourceId;
            source.SelectionChanged += (_, _) => command.ResponseSourceId = source.SelectedValue as string ?? "";
            transfer.Children.Add(new TextBlock { Text = "发送 CMD 后追加前序回复数据", Margin = new Thickness(0, 10, 0, 0) }); transfer.Children.Add(source);
            ComboBox sourceSide = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = new[] { EarChannel.Left, EarChannel.Right } };
            sourceSide.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, new Binding("ResponseSourceChannel") { Source = command, Mode = BindingMode.TwoWay }); transfer.Children.Add(sourceSide);
            ProductionProfileEditor.AddInput(transfer, "来源整帧偏移（从 0 开始）", command, "ResponseSourceOffset", 140);
            ProductionProfileEditor.AddInput(transfer, "截取字节数", command, "ResponseSourceLength", 140);
            body.Children.Add(new Expander { Header = "步骤协作 / 数据引用（可选）", Content = transfer, Margin = new Thickness(0, 12, 0, 0) });
        }
        ScrollViewer scroll = new() { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        DockPanel footer = new() { Margin = new Thickness(0, 16, 0, 0) };
        Button save = ProductionProfileEditor.ActionButton("保存命令", () => { try { Prepare(); ProductionProfile check = new() { Protocol = project, Commands = [.. previousSteps ?? [], command] }; check.Validate(); DialogResult = true; } catch (Exception e) { _error.Text = e.Message; } });
        DockPanel.SetDock(save, Dock.Right); footer.Children.Add(save); footer.Children.Add(_error); Grid.SetRow(footer, 1); root.Children.Add(footer); Content = root;
    }
    private static TextBox LengthBox(Panel parent, string label, int value)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        TextBox box = new() { Text = value < 0 ? "" : value.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) }; parent.Children.Add(box); return box;
    }
    private void Refresh() { _fields.ItemsSource = null; _fields.ItemsSource = _command.Fields; }
    private void Commit() { if (!_fields.CommitEdit(DataGridEditingUnit.Cell, true) || !_fields.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidOperationException("请检查字段输入"); }
    private void Prepare()
    {
        if (HasInvalidInput((DependencyObject)Content)) throw new InvalidOperationException("请检查标红的输入");
        Commit(); _command.TxLength = string.IsNullOrWhiteSpace(_txLength.Text) ? -1 : int.Parse(_txLength.Text, System.Globalization.CultureInfo.InvariantCulture);
        _command.RxLength = string.IsNullOrWhiteSpace(_rxLength.Text) ? -1 : int.Parse(_rxLength.Text, System.Globalization.CultureInfo.InvariantCulture);
        _command.MatchOffset = _command.Frame!.PayloadOffset;
        _command.Frame.Validate();
        if (string.IsNullOrWhiteSpace(_command.Name) || _command.DelayMs is < 0 or > 5000 || _command.TimeoutMs is < 50 or > 30000) throw new InvalidOperationException("请检查名称、间隔和超时");
        if (_command.RxLength >= 0) _command.FixedResponseLength = _command.RxLength + _command.Frame.LengthAdjustment;
        else _command.FixedResponseLength = 0;
        CommandPreview.ArrangeFields(_command, _project);
    }
    private static bool HasInvalidInput(DependencyObject node)
    {
        if (Validation.GetHasError(node)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (HasInvalidInput(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
    private void Calculate()
    {
        try
        {
            Prepare();
            if (_command.ResponseSourceId.Length > 0)
            {
                ProtocolSettings p = _command.Frame ?? _project;
                byte[] sample = new byte[_command.ResponseSourceOffset + _command.ResponseSourceLength + ProtocolCodec.CrcSize(p.Checksum)];
                _preview.Text = "发送示例：" + Hex.Format(ProtocolCodec.BuildFrame(p, _command, sample));
                _error.Text = "动态数据暂用 00 演算；实际发送使用本轮对耳应答，CRC 随数据变化。未发送串口。";
            }
            else { var result = CommandPreview.Calculate(_command, _project); _preview.Text = "发送：" + result.Tx + Environment.NewLine + "回复：" + result.Rx; _error.Text = "本地计算完成，未发送串口"; }
        }
        catch (Exception e) { _error.Text = e.Message; }
    }
}
