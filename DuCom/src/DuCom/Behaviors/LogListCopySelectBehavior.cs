using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuCom.ViewModels;

namespace DuCom.Behaviors;

public static class LogListCopySelectBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(LogListCopySelectBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty SelectAllCommandProperty = DependencyProperty.RegisterAttached(
        "SelectAllCommand",
        typeof(ICommand),
        typeof(LogListCopySelectBehavior),
        new PropertyMetadata(null, OnSelectAllCommandChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetSelectAllCommand(DependencyObject element, ICommand value) => element.SetValue(SelectAllCommandProperty, value);

    public static ICommand GetSelectAllCommand(DependencyObject element) => (ICommand)element.GetValue(SelectAllCommandProperty);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        listBox.PreviewKeyDown -= OnPreviewKeyDown;
        if ((bool)e.NewValue)
        {
            listBox.PreviewKeyDown += OnPreviewKeyDown;
        }
    }

    private static void OnSelectAllCommandChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (e.OldValue is ICommand oldCommand)
        {
            oldCommand.CanExecuteChanged -= listBox.OnCommandCanExecuteChanged;
        }

        if (e.NewValue is ICommand newCommand)
        {
            newCommand.CanExecuteChanged += listBox.OnCommandCanExecuteChanged;
        }
    }

    private static void OnCommandCanExecuteChanged(this ListBox listBox, object? sender, EventArgs e)
    {
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SelectAll(listBox);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelectedLines(listBox);
            e.Handled = true;
        }
    }

    public static void SelectAll(ListBox listBox)
    {
        listBox.SelectAll();
        if (GetSelectAllCommand(listBox) is ICommand command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    public static void CopySelectedLines(ListBox listBox)
    {
        IEnumerable<LogLineViewModel> selected = listBox.SelectedItems
            .OfType<LogLineViewModel>();
        if (!selected.Any())
        {
            selected = listBox.Items.OfType<LogLineViewModel>();
        }

        string text = string.Join(Environment.NewLine, selected.Select(line => line.Text));
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }
}
