using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DuCom.Plugins.Production;

namespace DuCom.Services.Plugins;

/// <summary>Compact host editor: send/reply wiring, inline reply fields, and an auto example.</summary>
public sealed class ProductionCommandEditor : Window
{
    private readonly ProductionCommand _command;
    private readonly ProtocolSettings _project;
    private readonly DataGrid _fields = new()
    {
        Height = 196, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = false,
        SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
    };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, MinWidth = 0 };
    private readonly TextBox _txPreview = PreviewBox("发送整帧示例");
    private readonly TextBox _rxPreview = PreviewBox("回复整帧示例");
    private readonly TextBox _parsed = PreviewBox("示例解析结果");
    private TextBox _txLength = null!;
    private TextBox _rxLength = null!;
    private readonly IReadOnlyList<ProductionCommand> _previous;

    public ProductionCommandEditor(ProductionCommand command, ProtocolSettings project, IReadOnlyList<ProductionCommand>? previousSteps = null)
    {
        _command = command;
        _project = project;
        _previous = previousSteps ?? [];
        command.Frame ??= ProductionProfileEditor.Clone(project);
        foreach (ResponseField field in command.Fields) field.Normalize();
        Title = "命令详情";
        Width = 980; Height = 780; MinWidth = 820; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/DuCom;component/Services/Plugins/ProductionStyles.xaml", UriKind.Relative) });
        PluginVisualTheme.Apply(this);
        // Pixel-aligned text: this dialog scrolls a lot of labels; Display mode keeps
        // glyph rasterization on the fast path instead of re-rasterizing per frame.
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        _parsed.SetResourceReference(TextBox.ForegroundProperty, "Brush.Accent");
        MaxHeight = Math.Max(1, SystemParameters.WorkArea.Height - 24);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Height = Math.Min(Height, MaxHeight);

        Grid root = new() { Margin = new Thickness(24, 16, 24, 16) };
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        StackPanel body = new();
        Section(body, "命令与时间");
        WrapPanel common = new();
        Input(common, "名称", command, nameof(command.Name), 250);
        Input(common, "间隔 ms", command, nameof(command.DelayMs), 110);
        Input(common, "超时 ms", command, nameof(command.TimeoutMs), 120);
        CheckInline(common, "等待并解析回复", command, nameof(command.WaitResponse),
            "发送后等待设备回复并解析下面的字段；不勾选则只发送、不读回复。");
        body.Children.Add(common);
        Section(body, "发送");
        WrapPanel tx = new();
        Input(tx, "帧头 HEX", command.Frame, nameof(ProtocolSettings.TxHeader), 140);
        Input(tx, "CMD / 数据 HEX", command, nameof(command.Hex), 320);
        CheckInline(tx, "完整帧直发", command, nameof(command.RawFrame));
        body.Children.Add(tx);
        Section(body, "回复");
        WrapPanel rx = new();
        Input(rx, "帧头 HEX", command.Frame, nameof(ProtocolSettings.RxHeader), 140);
        TextBox match = Input(rx, "匹配 CMD / 内容 HEX", command, nameof(command.ResponseMatch), 220);
        TextBox matchOffset = Input(rx, "匹配偏移", command, nameof(command.MatchOffset), 100);
        matchOffset.ToolTip = "从回复整帧的第 0 字节开始数。例如回复 55 AA 08 98 02 …：字节0=55、字节1=AA、字节2=08（长度）、字节3=98 —— 偏移 3 就是从 98 开始匹配上面的内容。";
        body.Children.Add(rx);
        TextBlock rxHint = new()
        {
            Text = "不知道帧结构？把设备实际收发的两帧粘贴到下方“自动识别”，帧头、长度、校验会自动算好。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            FontSize = 12,
        };
        rxHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        body.Children.Add(rxHint);
        body.Children.Add(new Expander
        {
            Header = "高级：帧结构 / 校验 / 状态 / 长度值",
            Margin = new Thickness(0, 4, 0, 6),
            Content = Advanced(command, command.Frame!),
        });
        Section(body, "回复字段", "每个字段 = 从回复帧取一段数据转成可读结果。偏移从整帧第 0 字节开始。新增字段默认 HEX，类型在行内下拉自行选择（MAC 固定 6 字节、整数长度随类型）；STR/文本按 UTF-8 解码、兼容 GBK。示例 HEX 可留空（留空时用 00 演算）。");
        WrapPanel tools = new() { Margin = new Thickness(0, 0, 0, 8) };
        // Production convention: every new field starts as HEX; the operator picks the
        // real type (Mac / UInt8 / Str …) from the row's 类型 dropdown afterwards.
        tools.Children.Add(Action("添加字段", () => AddField(FieldType.Hex, "数据")));
        tools.Children.Add(Action("删除", () => { if (_fields.SelectedItem is ResponseField f) { command.Fields.Remove(f); RefreshFields(); } }));
        tools.Children.Add(Action("上移", () => MoveField(-1)));
        tools.Children.Add(Action("下移", () => MoveField(1)));
        tools.Children.Add(Action("按顺序排列偏移", () => { CommandPreview.ArrangeFields(command, project); RefreshFields(); }));
        body.Children.Add(tools);
        BuildColumns();
        DuCom.Behaviors.NestedScrollWheelBehavior.Attach(_fields);
        _fields.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(NormalizeEditedField));
        _fields.RowEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(NormalizeEditedField));
        body.Children.Add(_fields);
        Section(body, "示例");
        WrapPanel exampleTools = new() { Margin = new Thickness(0, 0, 0, 8) };
        exampleTools.Children.Add(Action("刷新示例", Calculate));
        body.Children.Add(exampleTools);
        body.Children.Add(Labeled("发送整帧", _txPreview));
        body.Children.Add(Labeled("回复整帧（示例数据）", _rxPreview));
        body.Children.Add(Labeled("解析结果", _parsed));
        if (previousSteps is not null) AddTransfer(body, previousSteps);
        ScrollViewer scroll = new() { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.Children.Add(scroll);

        Grid footer = new() { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        StackPanel actions = new() { Orientation = Orientation.Horizontal };
        actions.Children.Add(Action("自动识别", () =>
        {
            ProductionAutoDetectDialog dialog = new(_command) { Owner = this };
            if (dialog.ShowDialog() == true) { RefreshFields(); Calculate(); }
        }));
        actions.Children.Add(Action("取消", Close));
        actions.Children.Add(Action("保存命令", () =>
        {
            try { Prepare(); new ProductionProfile { Protocol = project, Commands = [.. _previous, command] }.Validate(); DialogResult = true; }
            catch (Exception e) { _error.Text = e.Message; }
        }));
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); footer.Children.Add(_error);
        Grid.SetRow(footer, 1); root.Children.Add(footer);
        Content = root;
        RefreshFields();
        Loaded += (_, _) => { try { Calculate(); } catch (Exception e) { _error.Text = e.Message; } };
    }

    private FrameworkElement Advanced(ProductionCommand command, ProtocolSettings frame)
    {
        StackPanel advanced = new() { Margin = new Thickness(0, 8, 0, 0) };
        WrapPanel lengths = new();
        _txLength = LengthBox(lengths, "发送长度值（留空自动）", command.TxLength);
        _rxLength = LengthBox(lengths, "回复长度值（留空自动）", command.RxLength >= 0 ? command.RxLength
            : command.FixedResponseLength > 0 ? command.FixedResponseLength - frame.LengthAdjustment : -1);
        advanced.Children.Add(lengths);
        WrapPanel framePanel = new();
        Input(framePanel, "长度偏移", frame, nameof(ProtocolSettings.LengthOffset), 100);
        Input(framePanel, "长度字节", frame, nameof(ProtocolSettings.LengthSize), 100);
        Input(framePanel, "长度调整值", frame, nameof(ProtocolSettings.LengthAdjustment), 110);
        Input(framePanel, "载荷偏移", frame, nameof(ProtocolSettings.PayloadOffset), 100);
        advanced.Children.Add(framePanel);
        WrapPanel checksum = new();
        ComboBox crc = new() { Width = 170, Margin = new Thickness(0) };
        crc.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, TwoWay(frame, nameof(ProtocolSettings.Checksum)));
        Field(checksum, "校验算法", crc);
        Input(checksum, "校验起点", frame, nameof(ProtocolSettings.CrcStart), 100);
        Input(checksum, "校验尾部排除", frame, nameof(ProtocolSettings.CrcEndTrim), 120);
        advanced.Children.Add(checksum);
        WrapPanel status = new();
        Input(status, "状态偏移（-1 关闭）", command, nameof(command.StatusOffset), 150);
        Input(status, "成功状态值", command, nameof(command.StatusValue), 120);
        advanced.Children.Add(status);
        Check(advanced, "长度字段小端", frame, nameof(ProtocolSettings.LengthLittleEndian));
        Check(advanced, "CRC 小端", frame, nameof(ProtocolSettings.CrcLittleEndian));
        return advanced;
    }

    private void BuildColumns()
    {
        _fields.Columns.Add(new DataGridTextColumn { Header = "名称", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        // WPF's default combo editing style slaps SystemColors.Window (white) and
        // IsEditable=true onto the editing element as local values, which overrides the
        // dark theme. Neutralize it: transparent so the themed cell chrome shows through.
        Style typeEditing = new(typeof(ComboBox));
        typeEditing.Setters.Add(new Setter(ComboBox.IsEditableProperty, false));
        typeEditing.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        typeEditing.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        _fields.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "类型", ItemsSource = Enum.GetValues<FieldType>(),
            SelectedItemBinding = new Binding("Type") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 92, EditingElementStyle = typeEditing,
        });
        _fields.Columns.Add(new DataGridTextColumn { Header = "偏移", Binding = new Binding("Offset") { ValidatesOnExceptions = true }, Width = 62 });
        _fields.Columns.Add(new DataGridTextColumn { Header = "长度", Binding = new Binding("EffectiveLength") { Mode = BindingMode.OneWay }, IsReadOnly = true, Width = 56 });
        _fields.Columns.Add(new DataGridCheckBoxColumn { Header = "到末尾", Binding = new Binding("ToEnd"), Width = 64 });
        _fields.Columns.Add(new DataGridCheckBoxColumn { Header = "小端", Binding = new Binding("LittleEndian"), Width = 56 });
        _fields.Columns.Add(new DataGridCheckBoxColumn { Header = "反转", Binding = new Binding("Reverse"), Width = 56 });
        _fields.Columns.Add(new DataGridTextColumn { Header = "单位", Binding = new Binding("Unit"), Width = 64 });
        _fields.Columns.Add(new DataGridTextColumn { Header = "示例 HEX（可选）", Binding = new Binding("Example"), Width = 170 });
        AutomationProperties.SetName(_fields, "回复解析字段列表");
    }

    private void NormalizeEditedField()
    {
        if (_fields.SelectedItem is ResponseField field)
        {
            field.Normalize();
            if (field.FixedLength is int size) { field.Length = size; field.ToEnd = false; }
        }
        // CollectionView.Refresh() throws while an AddNew/EditItem transaction is open:
        // the CellEditEnding deferral can land after the user already moved into the
        // next cell of the same row (Tab / adjacent click), which keeps the row edit
        // running. Skip that pass; the RowEditEnding deferral refreshes after the row
        // edit has fully committed.
        if (_fields.Items is IEditableCollectionView { IsAddingNew: false, IsEditingItem: false })
            _fields.Items.Refresh();
    }

    private static Binding TwoWay(object source, string property) => new(property)
    { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true };

    private static TextBox PreviewBox(string label)
    {
        TextBox box = new() { IsReadOnly = true, AcceptsReturn = true, MinHeight = 40, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0) };
        AutomationProperties.SetName(box, label); return box;
    }
    private static FrameworkElement Labeled(string label, FrameworkElement content)
    {
        StackPanel panel = new() { Margin = new Thickness(0, 0, 0, 8) };
        TextBlock caption = new() { Text = label, Margin = new Thickness(0, 0, 0, 3) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(caption); panel.Children.Add(content); return panel;
    }
    private static void Section(Panel panel, string title, string? hint = null)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) });
        if (hint is null) return;
        TextBlock hintBlock = new() { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), FontSize = 12 };
        hintBlock.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(hintBlock);
    }
    private static void Field(Panel panel, string label, FrameworkElement control)
    {
        StackPanel field = new() { Margin = new Thickness(0, 0, 12, 8) };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
        AutomationProperties.SetName(control, label); field.Children.Add(control); panel.Children.Add(field);
    }
    private static TextBox Input(Panel panel, string label, object source, string property, double width)
    {
        TextBox box = new() { Width = width, Margin = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
        box.SetBinding(TextBox.TextProperty, TwoWay(source, property)); Field(panel, label, box); return box;
    }
    private static CheckBox CheckInline(Panel panel, string label, object source, string property, string? tooltip = null)
    {
        CheckBox box = new() { Content = label, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 12, 8), ToolTip = tooltip };
        AutomationProperties.SetName(box, label);
        box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, TwoWay(source, property)); panel.Children.Add(box); return box;
    }
    private static CheckBox Check(Panel panel, string label, object source, string property)
    {
        CheckBox box = new() { Content = label, Margin = new Thickness(0, 2, 12, 6) };
        AutomationProperties.SetName(box, label);
        box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, TwoWay(source, property)); panel.Children.Add(box); return box;
    }
    private Button Action(string label, Action action) => ProductionProfileEditor.ActionButton(label, () =>
    {
        try { action(); } catch (Exception error) { _error.Text = error.Message; }
    });
    private static TextBox LengthBox(Panel parent, string label, int value)
    {
        TextBox box = new() { Text = value < 0 ? "" : value.ToString(CultureInfo.InvariantCulture), Width = 150, Margin = new Thickness(0) };
        Field(parent, label, box); return box;
    }
    private void AddField(FieldType type, string name)
    {
        int start = _command.MatchOffset + Hex.Parse(_command.ResponseMatch).Length;
        int offset = _command.Fields.Select(f => f.Offset + f.EffectiveLength).DefaultIfEmpty(start).Max();
        ResponseField field = new() { Name = name, Type = type, Offset = offset };
        if (field.FixedLength is int length) field.Length = length;
        _command.Fields.Add(field); RefreshFields(field);
    }
    private void MoveField(int delta)
    {
        if (_fields.SelectedItem is not ResponseField field) return;
        int from = _command.Fields.IndexOf(field), to = from + delta;
        if (to < 0 || to >= _command.Fields.Count) return;
        (_command.Fields[from], _command.Fields[to]) = (_command.Fields[to], _command.Fields[from]);
        RefreshFields(field);
    }
    private void RefreshFields(ResponseField? selected = null)
    {
        selected ??= _fields.SelectedItem as ResponseField;
        _fields.ItemsSource = null; _fields.ItemsSource = _command.Fields;
        _fields.SelectedItem = selected is not null && _command.Fields.Contains(selected) ? selected : _command.Fields.FirstOrDefault();
    }
    private void AddTransfer(Panel body, IReadOnlyList<ProductionCommand> previousSteps)
    {
        StackPanel transfer = new() { Margin = new Thickness(0, 0, 0, 0) };
        ComboBox route = new() { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, DisplayMemberPath = "Label", SelectedValuePath = "Key",
            ItemsSource = new[] { new { Key = "selected", Label = "跟随主页所选通道" }, new { Key = "left", Label = "固定左耳（双耳协作）" }, new { Key = "right", Label = "固定右耳（双耳协作）" } } };
        route.SelectedValue = _command.PairingStep ? _command.Channel == EarChannel.Right ? "right" : "left" : "selected";
        route.SelectionChanged += (_, _) => { _command.PairingStep = (string?)route.SelectedValue != "selected"; _command.Channel = (string?)route.SelectedValue == "right" ? EarChannel.Right : _command.PairingStep ? EarChannel.Left : EarChannel.Both; };
        Field(transfer, "执行位置", route);
        ComboBox source = new() { Width = 300, DisplayMemberPath = "Name", SelectedValuePath = "Id",
            ItemsSource = new[] { new { Id = "", Name = "不追加来源数据" } }.Concat(previousSteps.Select(s => new { s.Id, s.Name })).ToArray() };
        source.SelectedValue = _command.ResponseSourceId;
        source.SelectionChanged += (_, _) => _command.ResponseSourceId = source.SelectedValue as string ?? "";
        Field(transfer, "追加前序回复", source);
        ComboBox side = new() { Width = 110, ItemsSource = new[] { EarChannel.Left, EarChannel.Right } };
        side.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedItemProperty, TwoWay(_command, nameof(_command.ResponseSourceChannel)));
        Field(transfer, "来源耳机", side);
        Input(transfer, "来源偏移", _command, nameof(_command.ResponseSourceOffset), 110);
        Input(transfer, "截取字节", _command, nameof(_command.ResponseSourceLength), 110);
        body.Children.Add(new Expander { Header = "双耳协作 / 数据引用（可选）", Content = transfer, Margin = new Thickness(0, 8, 0, 0) });
    }
    private void Prepare()
    {
        _fields.CommitEdit(DataGridEditingUnit.Cell, true);
        _fields.CommitEdit(DataGridEditingUnit.Row, true);
        if (HasInvalidInput((DependencyObject)Content)) throw new InvalidOperationException("请检查标红的输入，偏移和长度应为整数。");
        _command.TxLength = string.IsNullOrWhiteSpace(_txLength.Text) ? -1 : int.Parse(_txLength.Text, CultureInfo.InvariantCulture);
        _command.RxLength = string.IsNullOrWhiteSpace(_rxLength.Text) ? -1 : int.Parse(_rxLength.Text, CultureInfo.InvariantCulture);
        _command.Frame!.Validate();
        if (string.IsNullOrWhiteSpace(_command.Name) || _command.DelayMs is < 0 or > 5000 || _command.TimeoutMs is < 50 or > 30000)
            throw new InvalidOperationException("名称不能为空；间隔 0–5000 ms，超时 50–30000 ms。");
        _command.FixedResponseLength = _command.RxLength >= 0 ? _command.RxLength + _command.Frame.LengthAdjustment : 0;
        foreach (ResponseField field in _command.Fields) { field.Normalize(); field.Validate(); }
        _fields.Items.Refresh();
    }
    private static bool HasInvalidInput(DependencyObject node)
    {
        if (Validation.GetHasError(node)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (HasInvalidInput(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
    private void Calculate()
    {
        Prepare();
        if (_command.RawFrame)
        {
            // Raw frames keep their exact bytes; the host cannot invent a matching reply.
            _txPreview.Text = Hex.Format(ProtocolCodec.BuildFrame(_command.Frame!, _command));
            _rxPreview.Text = "完整帧直发：不自动生成回复示例。";
            _parsed.Text = "按设备实际回复校验。";
            _error.Text = "配置可保存并照常发送。";
            return;
        }
        if (_command.ResponseSourceId.Length > 0)
        {
            ProtocolSettings p = _command.Frame!;
            byte[] sample = new byte[_command.ResponseSourceOffset + _command.ResponseSourceLength + ProtocolCodec.CrcSize(p.Checksum)];
            _txPreview.Text = Hex.Format(ProtocolCodec.BuildFrame(p, _command, sample));
            _rxPreview.Text = "含前序回复数据，实际报文随本轮来源变化。";
            _parsed.Text = "动态来源暂用 00 演算。";
            _error.Text = "未发送串口。";
            return;
        }
        // Build an example reply for every command without touching saved examples.
        ProductionCommand example = ProductionProfileEditor.Clone(_command);
        foreach (ResponseField field in example.Fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Example)) continue;
            field.Example = Hex.Format(SampleBytes(field));
        }
        var preview = CommandPreview.Calculate(example, _project);
        _txPreview.Text = preview.Tx;
        _rxPreview.Text = preview.Rx;
        _parsed.Text = example.Fields.Count == 0 ? "已应答" : ProtocolCodec.Decode(example, Hex.Parse(preview.Rx), example.Frame ?? _project);
        _error.Text = "示例为本地演算，未发送串口。";
    }
    private static byte[] SampleBytes(ResponseField field) => field.Type switch
    {
        FieldType.Mac => [0x11, 0x22, 0x33, 0x44, 0x55, 0x66],
        FieldType.UInt8 => [0x4B],
        FieldType.UInt16 or FieldType.Int16 => [0x0F, 0x75],
        FieldType.UInt32 or FieldType.Int32 => [0x00, 0x00, 0x30, 0x39],
        _ => field.ToEnd ? [0x44, 0x75, 0x43, 0x6F, 0x6D] : new byte[Math.Max(1, field.EffectiveLength)],
    };
}
