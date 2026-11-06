using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace DuCom.Services.Plugins;

/// <summary>Presentation-only adaptation of BES's existing controls. Retains their tags, events and state.</summary>
internal static class BesCompactPresentation
{
    private const string BesFirmwarePathTag = "bes-firmware-path";

    internal static bool Matches(string? pluginId) => string.Equals(pluginId, "internal.ducom.bes-download", StringComparison.Ordinal);

    internal static void Apply(FrameworkElement root)
    {
        ResourceDictionary styles = new() { Source = new Uri("/DuCom;component/Services/Plugins/BesCompactStyles.xaml", UriKind.Relative) };
        root.Resources.MergedDictionaries.Add(styles);
        // BES 生产工具字号固定 16，不随密度令牌浮动，避免不同显示器上字号不一致。
        TextElement.SetFontSize(root, 18d);
        Visit(root);

        void Visit(FrameworkElement element)
        {
            if (element is Panel panel)
            {
                for (int index = 0; index < panel.Children.Count; index++)
                {
                    if (panel.Children[index] is not Panel child) continue;
                    string? identity = Find<TextBox>(child).Select(box => box.Tag as string)
                        .FirstOrDefault(tag => tag is "bt-name" or "ble-name");
                    if (child is StackPanel { Orientation: Orientation.Horizontal } &&
                        (child.Children.OfType<CheckBox>().Any(box => Equals(box.Tag, "erase-chip")) ||
                         child.Children.OfType<Button>().Any(button => Equals(button.Content, "刷新串口"))))
                    {
                        WrapPanel row = new() { Margin = new Thickness(0) };
                        foreach (UIElement control in child.Children.Cast<UIElement>().ToArray())
                        {
                            child.Children.Remove(control);
                            // 状态提示与三个按钮拉开 120px，避免"配置已更新。"紧贴"停止烧录"。
                            if (control is Panel { Width: 80 } spacer) spacer.Width = 120;
                            row.Children.Add(control);
                        }
                        panel.Children.RemoveAt(index);
                        panel.Children.Insert(index, Card(row));
                    }
                    else if (identity is not null && Find<TextBox>(child).Count() == 2)
                    {
                        Grid row = IdentityRow(child, identity.StartsWith("bt", StringComparison.Ordinal) ? "bt" : "ble");
                        panel.Children.RemoveAt(index);
                        panel.Children.Insert(index, Card(row));
                    }
                    else if (child.Children.Count == 3 && child.Children[0] is CheckBox { Tag: "app-enabled" or "ota-enabled" })
                    {
                        Grid row = FirmwareRow(child);
                        panel.Children.RemoveAt(index);
                        panel.Children.Insert(index, row);
                    }
                }
            }
            switch (element)
            {
                case Button button:
                    bool primary = button.Content is string text && text is "开始烧录" or "正在校验";
                    button.Style = (Style)styles[primary ? "Bes.PrimaryButton" : "Bes.Button"];
                    button.Padding = new Thickness(10, 0, 10, 0);
                    break;
                case CheckBox box:
                    box.Style = (Style)styles[typeof(CheckBox)];
                    break;
                case ComboBox combo:
                    combo.Style = (Style)styles[typeof(ComboBox)];
                    combo.Height = 44;
                    combo.MinHeight = 44;
                    combo.FontSize = 18;
                    break;
                case Expander expander:
                    expander.Style = (Style)styles["Bes.Expander"];
                    break;
                case TextBox input:
                    // 高度必须跟着字号走，否则 16px 字被 30px 高的框上下切掉。
                    // 输入框字号比界面小一号：等宽数据用小字更紧凑，也避免行高把内容区顶满。
                    // 高度不写死，交由隐式样式的 MinHeight(42) 兜底 —— 写死的局部值会被容器的
                    // 拉伸逻辑压小，导致 16px 字被上下削掉只剩上半截。
                    input.FontSize = 16;
                    input.Padding = new Thickness(12, 6, 12, 6);
                    input.VerticalAlignment = VerticalAlignment.Center;
                    break;
                case TextBlock label:
                    if (label.Tag as string == BesFirmwarePathTag)
                    {
                        // 固件路径保持 14 号字（比界面小一号），不跟随通用字号。
                        label.FontSize = 14;
                        break;
                    }
                    label.FontSize = label.Tag is DuCom.Plugin.UiTextStyle.Heading ? 20 : 18;
                    break;
                case Border border when border.Style is not null:
                    border.Padding = new Thickness(16, 12, 16, 12);
                    border.Margin = new Thickness(0, 0, 0, 12);
                    break;
            }
            foreach (object child in LogicalTreeHelper.GetChildren(element))
                if (child is FrameworkElement descendant) Visit(descendant);
        }
    }

    private static Border Card(UIElement content)
    {
        Border card = new() { Child = content };
        if (Application.Current.TryFindResource("Style.SettingsCard") is Style style) card.Style = style;
        return card;
    }

    private static Grid IdentityRow(Panel source, string prefix)
    {
        CheckBox nameToggle = Find<CheckBox>(source).Single(box => Equals(box.Tag, $"write-{prefix}-name"));
        CheckBox macToggle = Find<CheckBox>(source).Single(box => Equals(box.Tag, $"write-{prefix}-mac"));
        TextBox name = Find<TextBox>(source).Single(box => Equals(box.Tag, $"{prefix}-name"));
        TextBox mac = Find<TextBox>(source).Single(box => Equals(box.Tag, $"{prefix}-start"));
        Button random = Find<Button>(source).Single();
        // 两列布局：NAME 组在左、MAC 组在右。窗口已加宽到 1400，
        // 配合密度字号，8 字节 MAC 与长设备名都能完整显示。
        Grid row = new() { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Attach(row, nameToggle, 0); Attach(row, name, 1); Attach(row, macToggle, 2); Attach(row, mac, 3); Attach(row, random, 4);
        name.Width = mac.Width = double.NaN;
        // 限制最大宽度：框不再无限拉伸，右侧留白由窗口吸收
        name.MaxWidth = 330; mac.MaxWidth = 390;
        name.Margin = new Thickness(6, 0, 20, 0); mac.Margin = new Thickness(6, 0, 10, 0);
        return row;
    }

    private static Grid FirmwareRow(Panel source)
    {
        UIElement[] controls = source.Children.Cast<UIElement>().ToArray();
        Grid row = new() { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        for (int index = 0; index < controls.Length; index++) Attach(row, controls[index], index);
        if (controls[2] is TextBlock path)
        {
            // 不再用负边距硬挤：路径从按钮右侧正常开始，只把右端内缩 24px。
            // 负边距会让长路径直接压在“更换固件”按钮文字上，窄窗口下尤其明显。
            path.Margin = new Thickness(0, 0, 24, 0);
            path.FontSize = 14;
            path.Tag = BesFirmwarePathTag;
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.TextWrapping = TextWrapping.NoWrap;
            path.ToolTip = path.Text;
        }
        return row;
    }

    private static void Attach(Grid row, UIElement child, int column, int rowIndex = 0)
    {
        if (LogicalTreeHelper.GetParent(child) is Panel parent) parent.Children.Remove(child);
        Grid.SetColumn(child, column);
        Grid.SetRow(child, rowIndex);
        row.Children.Add(child);
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : FrameworkElement
    {
        if (root is T match) yield return match;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node)
                foreach (T descendant in Find<T>(node)) yield return descendant;
    }
}
