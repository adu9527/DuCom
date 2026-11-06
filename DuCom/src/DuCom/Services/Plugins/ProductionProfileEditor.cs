using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DuCom.Plugins.Production;
using Microsoft.Win32;

namespace DuCom.Services.Plugins;

public sealed class ProductionProfileEditor : Window
{
    public ProductionProfile Profile { get; }
    private readonly DataGrid _table = new();
    private readonly TextBlock _status = new() { Foreground = (Brush)Application.Current.FindResource("Brush.Danger") };
    public ProductionProfileEditor(ProductionProfile profile)
    {
        Profile = profile;
        Title = "项目配置"; Width = 960; Height = 660; MinWidth = 800; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(9, 9, 11)); Foreground = Brushes.WhiteSmoke; FontSize = 13;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/DuCom;component/Services/Plugins/ProductionStyles.xaml", UriKind.Relative) });
        PluginVisualTheme.Apply(this);
        Grid root = new() { Margin = new Thickness(24) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        WrapPanel settings = new() { Margin = new Thickness(0, 0, 0, 16) };
        AddInput(settings, "项目", Profile, "Name", 160);
        AddInput(settings, "波特率", Profile.Protocol, "BaudRate", 85);
        AddInput(settings, "每轮间隔 ms", Profile, "LoopIntervalMs", 70);
        AddInput(settings, "取走检测超时 ms", Profile, "RemovalTimeoutMs", 90).ToolTip = "连续产测完成后的单侧检测超时；不影响正式命令超时。左右分别超时一次后复位。";
        root.Children.Add(settings);
        WrapPanel tools = new() { Margin = new Thickness(0, 0, 0, 12) };
        tools.Children.Add(ActionButton("添加命令", () =>
        {
            ProductionCommand c = new() { Channel = EarChannel.Both, Frame = Clone(Profile.Protocol), Hex = "A6 01", ResponseMatch = "A6 02", Name = "打开ANC", DelayMs = 200 };
            if (new ProductionCommandEditor(c, Profile.Protocol) { Owner = this }.ShowDialog() == true) { Profile.Commands.Add(c); Refresh(); }
        }));
        tools.Children.Add(ActionButton("删除", () => { if (_table.SelectedItem is ProductionCommand c) { Profile.Commands.Remove(c); Refresh(); } }));
        tools.Children.Add(ActionButton("添加复合命令", () =>
        {
            ProductionCommand c = new() { Name = "复合命令", Channel = EarChannel.Both, Steps = [new() { Name = "子命令1", Channel = EarChannel.Both }] };
            if (new ProductionCompoundEditor(c, Profile.Protocol) { Owner = this }.ShowDialog() == true) { Profile.Commands.Add(c); Refresh(); }
        }));
        tools.Children.Add(ActionButton("上移", () => Move(-1))); tools.Children.Add(ActionButton("下移", () => Move(1)));
        tools.Children.Add(ActionButton("导入项目", Import)); tools.Children.Add(ActionButton("导出项目", Export));
        Grid.SetRow(tools, 1); root.Children.Add(tools);
        FrameworkElementFactory enabled = new(typeof(CheckBox));
        enabled.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        enabled.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        enabled.SetValue(FrameworkElement.MinWidthProperty, 24d);
        enabled.SetValue(FrameworkElement.HeightProperty, 38d);
        enabled.SetValue(AutomationProperties.NameProperty, "启用命令");
        enabled.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("Enabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        _table.Columns.Add(new DataGridTemplateColumn { Header = "启用", CellTemplate = new() { VisualTree = enabled }, Width = 64 });
        _table.PreviewMouseLeftButtonDown += (_, e) =>
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node is not null && node is not DataGridCell) node = VisualTreeHelper.GetParent(node);
            if (node is DataGridCell cell && cell.Column.DisplayIndex == 0 && cell.DataContext is ProductionCommand c)
            { c.Enabled = !c.Enabled; _table.Items.Refresh(); e.Handled = true; }
        };
        _table.Columns.Add(new DataGridTextColumn { Header = "命令名称", Binding = new Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _table.Columns.Add(new DataGridTextColumn { Header = "发送 CMD", Binding = new Binding("Hex"), IsReadOnly = true, Width = 140 });
        _table.Columns.Add(TimingColumn("间隔 ms", "DelayMs"));
        _table.Columns.Add(TimingColumn("超时 ms", "TimeoutMs"));
        FrameworkElementFactory detail = new(typeof(Button)); detail.SetValue(ContentControl.ContentProperty, "详情"); detail.SetValue(FrameworkElement.HeightProperty, 28d);
        detail.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is not Button { DataContext: ProductionCommand original }) return;
            ProductionCommand copy = Clone(original);
            Window editor = copy.Steps.Count > 0 ? new ProductionCompoundEditor(copy, Profile.Protocol) : new ProductionCommandEditor(copy, Profile.Protocol);
            editor.Owner = this;
            if (editor.ShowDialog() == true)
            { Profile.Commands[Profile.Commands.IndexOf(original)] = copy; Refresh(); }
        }));
        _table.Columns.Add(new DataGridTemplateColumn { Header = "", Width = 80, CellTemplate = new() { VisualTree = detail } });
        Grid.SetRow(_table, 2); root.Children.Add(_table); Refresh();
        DockPanel footer = new() { Margin = new Thickness(0, 16, 0, 0) };
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ActionButton("取消", () => DialogResult = false));
        buttons.Children.Add(ActionButton("保存项目", () =>
        {
            try { _table.CommitEdit(); _table.CommitEdit(DataGridEditingUnit.Row, true); if (HasErrors(_table)) throw new InvalidOperationException("请检查间隔和超时的数字输入"); Profile.Validate(); DialogResult = true; }
            catch (Exception e) { _status.Text = e.Message; }
        }));
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons); footer.Children.Add(_status);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
    }
    private static DataGridTemplateColumn TimingColumn(string title, string property)
    {
        FrameworkElementFactory box = new(typeof(TextBox));
        box.SetValue(FrameworkElement.HeightProperty, 32d);
        box.SetValue(Control.PaddingProperty, new Thickness(6, 3, 6, 3));
        box.SetValue(AutomationProperties.NameProperty, title);
        box.SetBinding(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        box.AddHandler(UIElement.GotKeyboardFocusEvent, new System.Windows.Input.KeyboardFocusChangedEventHandler((sender, _) => { if (sender is TextBox text) text.SelectAll(); }));
        return new() { Header = title, Width = 98, CellTemplate = new() { VisualTree = box } };
    }
    private static bool HasErrors(DependencyObject node)
    {
        if (Validation.GetHasError(node)) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (HasErrors(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
    internal static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProductionProfile.JsonOptions), ProductionProfile.JsonOptions)!;
    internal static Button ActionButton(string label, Action action)
    {
        Button b = new() { Content = label, Margin = new Thickness(0, 0, 6, 0) }; b.Click += (_, _) => action(); return b;
    }
    internal static TextBox AddInput(Panel panel, string label, object source, string property, double width = 180)
    {
        StackPanel field = new() { Margin = new Thickness(0, 0, 16, 10) };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        TextBox box = new() { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        box.SetBinding(TextBox.TextProperty, new Binding(property) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        field.Children.Add(box); panel.Children.Add(field); return box;
    }
    private void Refresh() { _table.ItemsSource = null; _table.ItemsSource = Profile.Commands; }
    private void Move(int delta)
    {
        int from = _table.SelectedIndex, to = from + delta;
        if (from < 0 || to < 0 || to >= Profile.Commands.Count) return;
        (Profile.Commands[from], Profile.Commands[to]) = (Profile.Commands[to], Profile.Commands[from]); Refresh(); _table.SelectedIndex = to;
    }
    private void Import()
    {
        OpenFileDialog d = new() { Filter = "项目配置 (*.json)|*.json" }; if (d.ShowDialog(this) != true) return;
        try
        {
            ProductionProfile p = ProductionProfile.FromJson(File.ReadAllText(d.FileName));
            Profile.Name = p.Name; Profile.Protocol = p.Protocol; Profile.Commands = p.Commands; Profile.LoopIntervalMs = p.LoopIntervalMs; Profile.RemovalTimeoutMs = p.RemovalTimeoutMs;
            // Reopen to refresh project-level bindings, retaining this dialog's result semantics.
            ProductionProfileEditor next = new(Profile) { Owner = Owner }; if (next.ShowDialog() == true) DialogResult = true; else Refresh();
        }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private void Export()
    {
        SaveFileDialog d = new() { Filter = "项目配置 (*.json)|*.json", FileName = Profile.Name + ".json" }; if (d.ShowDialog(this) != true) return;
        try { Profile.Validate(); File.WriteAllText(d.FileName, Profile.ToJson()); _status.Text = "已导出"; } catch (Exception e) { _status.Text = e.Message; }
    }
}
