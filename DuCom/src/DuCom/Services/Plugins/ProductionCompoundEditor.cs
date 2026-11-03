using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DuCom.Plugins.Production;

namespace DuCom.Services.Plugins;

public sealed class ProductionCompoundEditor : Window
{
    private readonly ProductionCommand _group;
    private readonly ProtocolSettings _protocol;
    private readonly DataGrid _steps = new();
    private readonly ComboBox _output = new() { Width = 240, DisplayMemberPath = "Label", SelectedValuePath = "Id" };
    private readonly TextBlock _error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
    public ProductionCompoundEditor(ProductionCommand group, ProtocolSettings protocol)
    {
        _group = group; _protocol = protocol;
        Title = "复合命令"; Width = 980; Height = 700; MinWidth = 850; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(9, 9, 11)); Foreground = Brushes.WhiteSmoke;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/DuCom;component/Services/Plugins/ProductionStyles.xaml", UriKind.Relative) });
        Grid root = new() { Margin = new Thickness(24) };
        foreach (GridLength height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) root.RowDefinitions.Add(new() { Height = height });
        WrapPanel basics = new(); ProductionProfileEditor.AddInput(basics, "复合命令名称", group, "Name", 240); ProductionProfileEditor.AddInput(basics, "完成后间隔 ms", group, "DelayMs", 120); root.Children.Add(basics);
        WrapPanel tools = new() { Margin = new Thickness(0, 6, 0, 12) };
        tools.Children.Add(ProductionProfileEditor.ActionButton("添加子命令", () => Edit(null)));
        tools.Children.Add(ProductionProfileEditor.ActionButton("复制", () =>
        {
            if (_steps.SelectedItem is not ProductionCommand source) return;
            Commit(); var copy = ProductionProfileEditor.Clone(source); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " 副本";
            group.Steps.Insert(group.Steps.IndexOf(source) + 1, copy); Refresh();
        }));
        tools.Children.Add(ProductionProfileEditor.ActionButton("删除", () => { if (_steps.SelectedItem is ProductionCommand step) { Commit(); group.Steps.Remove(step); Refresh(); } }));
        tools.Children.Add(ProductionProfileEditor.ActionButton("上移", () => Move(-1))); tools.Children.Add(ProductionProfileEditor.ActionButton("下移", () => Move(1)));
        Grid.SetRow(tools, 1); root.Children.Add(tools);
        FrameworkElementFactory enabled = new(typeof(CheckBox)); enabled.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding("Enabled") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        _steps.Columns.Add(new DataGridTemplateColumn { Header = "启用", Width = 50, CellTemplate = new() { VisualTree = enabled } });
        _steps.Columns.Add(new DataGridTextColumn { Header = "子命令", Binding = new Binding("Name"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _steps.Columns.Add(new DataGridTextColumn { Header = "发送 CMD", Binding = new Binding("Hex"), IsReadOnly = true, Width = 120 });
        _steps.Columns.Add(new DataGridTextColumn { Header = "间隔 ms", Binding = new Binding("DelayMs") { ValidatesOnExceptions = true }, Width = 85 });
        _steps.Columns.Add(new DataGridTextColumn { Header = "超时 ms", Binding = new Binding("TimeoutMs") { ValidatesOnExceptions = true }, Width = 85 });
        FrameworkElementFactory detail = new(typeof(Button)); detail.SetValue(ContentControl.ContentProperty, "详情 / 解析"); detail.SetValue(FrameworkElement.HeightProperty, 28d);
        detail.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) => { if (sender is Button { DataContext: ProductionCommand step }) Edit(step); }));
        _steps.Columns.Add(new DataGridTemplateColumn { Width = 110, CellTemplate = new() { VisualTree = detail } });
        Grid.SetRow(_steps, 2); root.Children.Add(_steps);
        StackPanel result = new() { Margin = new Thickness(0, 16, 0, 0) };
        WrapPanel outputLine = new(); outputLine.Children.Add(new TextBlock { Text = "主页输出", Margin = new Thickness(0, 0, 12, 0) }); outputLine.Children.Add(_output);
        _output.SelectionChanged += (_, _) => group.OutputStepId = _output.SelectedValue as string ?? "";
        result.Children.Add(outputLine);
        ProductionProfileEditor.AddInput(result, "完成提示（留空则汇总各步骤结果）", group, "SuccessText", 450);
        group.Comparisons.Clear();
        result.Children.Add(new TextBlock { Text = "按顺序执行子命令；每步可独立配置发送、回复、解析和时间。失败时显示具体步骤，左右结果分别输出。", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(result, 3); root.Children.Add(result);
        DockPanel footer = new() { Margin = new Thickness(0, 16, 0, 0) };
        Button save = ProductionProfileEditor.ActionButton("保存复合命令", () =>
        {
            try { Commit(); new ProductionProfile { Name = "复合命令", Protocol = protocol, Commands = [group] }.Validate(); DialogResult = true; }
            catch (Exception e) { _error.Text = e.Message; }
        });
        DockPanel.SetDock(save, Dock.Right); footer.Children.Add(save); footer.Children.Add(_error); Grid.SetRow(footer, 4); root.Children.Add(footer); Content = root; Refresh();
    }
    private void Edit(ProductionCommand? step)
    {
        try
        {
            Commit(); ProductionCommand copy = step is null ? new() { Name = "子命令" + (_group.Steps.Count + 1), Channel = EarChannel.Both } : ProductionProfileEditor.Clone(step);
            var previous = _group.Steps.Take(step is null ? _group.Steps.Count : _group.Steps.IndexOf(step)).ToArray();
            if (new ProductionCommandEditor(copy, _protocol, previous) { Owner = this }.ShowDialog() != true) return;
            if (step is null) _group.Steps.Add(copy); else _group.Steps[_group.Steps.IndexOf(step)] = copy;
            Refresh();
        }
        catch (Exception e) { _error.Text = e.Message; }
    }
    private void Refresh()
    {
        _steps.ItemsSource = null; _steps.ItemsSource = _group.Steps;
        string selected = _group.OutputStepId;
        _output.ItemsSource = new[] { new { Id = "", Label = "完成提示 / 汇总" } }.Concat(_group.Steps.Where(s => s.Enabled).Select(s => new { Id = s.Id, Label = s.Name + " 的解析结果" })).ToArray();
        _output.SelectedValue = selected; _group.OutputStepId = selected;
    }
    private void Commit()
    {
        if (!_steps.CommitEdit(DataGridEditingUnit.Cell, true) || !_steps.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidOperationException("请检查子命令输入");
    }
    private void Move(int delta)
    {
        try { Commit(); int i = _steps.SelectedIndex, j = i + delta; if (i < 0 || j < 0 || j >= _group.Steps.Count) return; (_group.Steps[i], _group.Steps[j]) = (_group.Steps[j], _group.Steps[i]); Refresh(); _steps.SelectedIndex = j; }
        catch (Exception e) { _error.Text = e.Message; }
    }
}
