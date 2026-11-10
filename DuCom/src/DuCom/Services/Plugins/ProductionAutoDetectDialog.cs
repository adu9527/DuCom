using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DuCom.Plugins.Production;

namespace DuCom.Services.Plugins;

/// <summary>
/// Paste a captured send/reply frame pair, auto-detect the frame structure, confirm the
/// proposed CRC/header/CMD choices, and apply them to the command being edited.
/// </summary>
public sealed class ProductionAutoDetectDialog : Window
{
    private readonly ProductionCommand _command;
    private readonly TextBox _sendFrame = PasteBox("发送帧整帧 HEX");
    private readonly TextBox _replyFrame = PasteBox("回复帧整帧 HEX");
    private readonly ComboBox _headerLength = new() { Width = 200, Margin = new Thickness(0), MinHeight = 32 };
    private readonly ComboBox _crc = new() { Width = 230, Margin = new Thickness(0), MinHeight = 32 };
    private readonly ComboBox _cmdLength = new() { Width = 230, Margin = new Thickness(0), MinHeight = 32 };
    private readonly WrapPanel _results = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, MinWidth = 0 };
    private readonly Button _apply = new() { Content = "应用到本命令", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
    private List<ProductionFrameCandidate> _common = [];
    private byte[] _send = [];
    private byte[] _reply = [];

    public ProductionAutoDetectDialog(ProductionCommand command)
    {
        _command = command;
        Title = "自动识别帧结构";
        Width = 780; SizeToContent = SizeToContent.Height;
        MinWidth = 640; MaxWidth = 900;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/DuCom;component/Services/Plugins/ProductionStyles.xaml", UriKind.Relative) });
        PluginVisualTheme.Apply(this);
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40);
        _apply.IsEnabled = false;

        StackPanel body = new() { Margin = new Thickness(24, 16, 24, 8) };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Text = "把设备实际收发的两帧原始 HEX 粘贴到下面（可含空格），点“识别”自动算出帧头、长度与校验。识别结果可直接应用，CMD 与解析字段之后仍可在详情里微调。",
        });
        body.Children.Add(Labeled("发送帧（粘贴完整 HEX）", _sendFrame));
        body.Children.Add(Labeled("回复帧（粘贴完整 HEX）", _replyFrame));
        WrapPanel tools = new() { Margin = new Thickness(0, 4, 0, 0) };
        tools.Children.Add(ActionButton("识别", Identify));
        body.Children.Add(tools);
        WrapPanel choices = new() { Margin = new Thickness(0, 4, 0, 0) };
        Field(choices, "帧头字节数", _headerLength);
        Field(choices, "CRC 校验算法", _crc);
        Field(choices, "CMD / 匹配内容字节数", _cmdLength);
        _results = choices;
        body.Children.Add(_results);
        body.Children.Add(_summary);
        Grid footer = new() { Margin = new Thickness(0, 8, 0, 16) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        StackPanel actions = new() { Orientation = Orientation.Horizontal };
        actions.Children.Add(ActionButton("取消", Close));
        Button apply = _apply;
        actions.Children.Add(apply);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); footer.Children.Add(_error);
        body.Children.Add(footer);
        Content = body;
        _apply.Click += (_, _) => { try { Apply(); } catch (Exception error) { _error.Text = error.Message; } };
        Loaded += (_, _) => _sendFrame.Focus();
    }

    private static TextBox PasteBox(string label)
    {
        TextBox box = new() { Width = 640, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = new System.Windows.Media.FontFamily("Consolas"), Margin = new Thickness(0), MinHeight = 32 };
        AutomationProperties.SetName(box, label); return box;
    }
    private static FrameworkElement Labeled(string label, FrameworkElement content)
    {
        StackPanel panel = new() { Margin = new Thickness(0, 0, 0, 8) };
        TextBlock caption = new() { Text = label, Margin = new Thickness(0, 0, 0, 3) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(caption); panel.Children.Add(content); return panel;
    }
    private static void Field(Panel panel, string label, FrameworkElement control)
    {
        StackPanel field = new() { Margin = new Thickness(0, 0, 14, 8) };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
        AutomationProperties.SetName(control, label); field.Children.Add(control); panel.Children.Add(field);
    }
    private Button ActionButton(string label, Action action) => ProductionProfileEditor.ActionButton(label, () =>
    {
        try { action(); } catch (Exception error) { _error.Text = error.Message; }
    });

    private static byte[] ParseHex(string text)
    {
        StringBuilder compact = new(text.Length);
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character) || character is ':' or ',' or '-') continue;
            if (character is 'x' or 'X') continue;
            compact.Append(character);
        }
        if (compact.Length % 2 != 0) compact.Insert(0, '0');
        return Convert.FromHexString(compact.ToString());
    }

    private sealed record Option(int Value, string Text);

    private void Identify()
    {
        _error.Text = "";
        _send = ParseHex(_sendFrame.Text);
        _reply = ParseHex(_replyFrame.Text);
        if (_send.Length < 6 || _reply.Length < 6) throw new InvalidOperationException("两帧都请粘贴完整 HEX（每帧至少 6 字节）。");
        _common = ProductionFrameDetector.DetectPair(_send, _reply);
        if (_common.Count == 0)
        {
            _summary.Visibility = Visibility.Collapsed;
            _results.Visibility = Visibility.Collapsed;
            _apply.IsEnabled = false;
            throw new InvalidOperationException("两帧的帧结构或校验对不上：请确认两帧完整（含帧头、长度、CRC）且来自同一协议。");
        }

        _headerLength.ItemsSource = _common
            .Select(candidate => candidate.HeaderLength)
            .Distinct().OrderBy(value => value)
            .Select(value => new Option(value, $"{value} 字节（{Hex.Format(_send.AsSpan(0, value).ToArray())}）")).ToList();
        _headerLength.DisplayMemberPath = nameof(Option.Text);
        _headerLength.SelectedValuePath = nameof(Option.Value);
        _headerLength.SelectedIndex = 0;
        FillCrcOptions();
        int payloadLength = _send.Length - Selected.PayloadOffset - ProtocolCodec.CrcSize(Selected.Crc);
        _cmdLength.ItemsSource = Enumerable.Range(1, Math.Max(1, Math.Min(6, payloadLength)))
            .Select(length => new Option(length, $"{length} 字节（{Hex.Format(_send.AsSpan(Selected.PayloadOffset, Math.Min(length, payloadLength)).ToArray())}）")).ToList();
        _cmdLength.DisplayMemberPath = nameof(Option.Text);
        _cmdLength.SelectedValuePath = nameof(Option.Value);
        _cmdLength.SelectedValue = payloadLength >= 2 ? 2 : 1;
        _headerLength.SelectionChanged += (_, _) => { if (_headerLength.HasItems) FillCrcOptions(); };

        _results.Visibility = Visibility.Visible;
        _summary.Visibility = Visibility.Visible;
        UpdateSummary();
        _apply.IsEnabled = true;
    }

    private ProductionFrameCandidate Selected
    {
        get
        {
            int headerLength = _headerLength.SelectedValue is int header ? header : _common[0].HeaderLength;
            int crc = _crc.SelectedValue is int kind ? kind : (int)_common[0].Crc;
            return _common.First(candidate => candidate.HeaderLength == headerLength && (int)candidate.Crc == crc);
        }
    }

    private void FillCrcOptions()
    {
        int headerLength = _headerLength.SelectedValue is int header ? header : _common[0].HeaderLength;
        int previous = _crc.SelectedValue is int kind ? kind : (int)ChecksumKind.Crc8Maxim;
        List<Option> options = _common
            .Where(candidate => candidate.HeaderLength == headerLength)
            .Select(candidate => new Option((int)candidate.Crc, CrcText(candidate)))
            .GroupBy(option => option.Value).Select(group => group.First()).ToList();
        _crc.ItemsSource = options;
        _crc.DisplayMemberPath = nameof(Option.Text);
        _crc.SelectedValuePath = nameof(Option.Value);
        _crc.SelectedValue = options.Any(option => option.Value == previous) ? previous : options[0].Value;
    }

    private void UpdateSummary()
    {
        ProductionFrameCandidate candidate = Selected;
        int matchLength = Math.Min(2, Math.Max(0, _reply.Length - candidate.PayloadOffset));
        _summary.Text = $"识别结果：长度值={candidate.LengthValue}，长度调整={candidate.LengthAdjustment}，匹配偏移={candidate.PayloadOffset}，"
            + $"回复匹配内容={Hex.Format(_reply.AsSpan(candidate.PayloadOffset, matchLength).ToArray())}。确认上面的选择后点“应用到本命令”。";
    }

    private static string CrcText(ProductionFrameCandidate candidate) =>
        candidate.Crc + (ProtocolCodec.CrcSize(candidate.Crc) == 0 ? "（无校验）" : candidate.CrcLittleEndian ? "（小端）" : "");

    private void Apply()
    {
        ProductionFrameCandidate candidate = Selected;
        int cmdLength = _cmdLength.SelectedValue is int length ? length : 2;
        ProtocolSettings frame = _command.Frame ??= new();
        frame.TxHeader = Hex.Format(candidate.Header(_send));
        frame.RxHeader = Hex.Format(candidate.Header(_reply));
        frame.LengthOffset = candidate.LengthOffset;
        frame.LengthSize = candidate.LengthSize;
        frame.LengthAdjustment = candidate.LengthAdjustment;
        frame.PayloadOffset = candidate.PayloadOffset;
        frame.Checksum = candidate.Crc;
        frame.CrcStart = candidate.CrcStart;
        frame.CrcEndTrim = candidate.CrcEndTrim;
        frame.CrcLittleEndian = candidate.CrcLittleEndian;
        frame.LengthLittleEndian = candidate.LengthLittleEndian;
        _command.Hex = Hex.Format(_send.AsSpan(candidate.PayloadOffset, _send.Length - candidate.PayloadOffset - ProtocolCodec.CrcSize(candidate.Crc)).ToArray());
        int matchLength = Math.Min(cmdLength, Math.Max(0, _reply.Length - candidate.PayloadOffset));
        _command.ResponseMatch = Hex.Format(_reply.AsSpan(candidate.PayloadOffset, matchLength).ToArray());
        _command.MatchOffset = candidate.PayloadOffset;
        if (_command.Fields.Count == 0)
        {
            // Seed a first field right after the matched CMD so the operator only renames it.
            _command.Fields.Add(new ResponseField
            {
                Name = "数值",
                Type = FieldType.Hex,
                Offset = candidate.PayloadOffset + matchLength,
                Length = Math.Max(1, _reply.Length - candidate.PayloadOffset - matchLength - ProtocolCodec.CrcSize(candidate.Crc)),
            });
        }
        DialogResult = true;
        Close();
    }
}
