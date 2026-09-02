using System.Windows;
using System.Windows.Controls;
using DuCom.Core.Search;
using DuCom.ViewModels;

namespace DuCom.Behaviors;

public static class ListBoxSearchScrollBehavior
{
    public static readonly DependencyProperty CurrentMatchProperty = DependencyProperty.RegisterAttached(
        "CurrentMatch",
        typeof(SearchMatch?),
        typeof(ListBoxSearchScrollBehavior),
        new PropertyMetadata(null, OnCurrentMatchChanged));

    public static SearchMatch? GetCurrentMatch(DependencyObject obj) =>
        (SearchMatch?)obj.GetValue(CurrentMatchProperty);

    public static void SetCurrentMatch(DependencyObject obj, SearchMatch? value) =>
        obj.SetValue(CurrentMatchProperty, value);

    private static void OnCurrentMatchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox || e.NewValue is not SearchMatch match)
        {
            return;
        }

        LogLineViewModel? target = listBox.Items
            .OfType<LogLineViewModel>()
            .FirstOrDefault(item => item.LogicalId == match.LogicalId && item.SegmentIndex == match.SegmentIndex)
            ?? listBox.Items.OfType<LogLineViewModel>().FirstOrDefault(item => item.LogicalId == match.LogicalId);

        if (target is not null)
        {
            listBox.SelectedItem = target;
            listBox.ScrollIntoView(target);
        }
    }
}
