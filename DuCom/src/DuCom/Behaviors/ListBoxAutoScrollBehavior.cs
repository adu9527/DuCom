using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DuCom.Behaviors;

public static class ListBoxAutoScrollBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(ListBoxAutoScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(AutoScrollState),
        typeof(ListBoxAutoScrollBehavior));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            AutoScrollState state = new(listBox);
            listBox.SetValue(StateProperty, state);
            listBox.Loaded += OnLoaded;
            listBox.Unloaded += OnUnloaded;

            if (listBox.IsLoaded)
            {
                state.Activate();
            }
        }
        else
        {
            listBox.Loaded -= OnLoaded;
            listBox.Unloaded -= OnUnloaded;
            GetState(listBox)?.Dispose();
            listBox.ClearValue(StateProperty);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => GetState((ListBox)sender)?.Activate();

    private static void OnUnloaded(object sender, RoutedEventArgs e) => GetState((ListBox)sender)?.Deactivate();

    private static AutoScrollState? GetState(ListBox listBox) =>
        (AutoScrollState?)listBox.GetValue(StateProperty);

    private sealed class AutoScrollState : IDisposable
    {
        private static readonly DependencyPropertyDescriptor ItemsSourceDescriptor =
            DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ListBox));

        private readonly ListBox _listBox;
        private readonly CoalescedActionGate _scrollGate = new();
        private INotifyCollectionChanged? _collection;
        private DispatcherOperation? _pendingOperation;
        private bool _isActive;

        public AutoScrollState(ListBox listBox)
        {
            _listBox = listBox;
        }

        public void Activate()
        {
            if (_isActive)
            {
                return;
            }

            _isActive = true;
            ItemsSourceDescriptor.AddValueChanged(_listBox, OnItemsSourceChanged);
            SubscribeToCurrentCollection();
        }

        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            CancelPendingScroll();
            UnsubscribeFromCollection();
            ItemsSourceDescriptor.RemoveValueChanged(_listBox, OnItemsSourceChanged);
        }

        public void Dispose() => Deactivate();

        private void OnItemsSourceChanged(object? sender, EventArgs e)
        {
            CancelPendingScroll();
            UnsubscribeFromCollection();
            SubscribeToCurrentCollection();
        }

        private void SubscribeToCurrentCollection()
        {
            _collection = _listBox.ItemsSource as INotifyCollectionChanged;
            if (_collection is not null)
            {
                _collection.CollectionChanged += OnCollectionChanged;
            }
        }

        private void UnsubscribeFromCollection()
        {
            if (_collection is not null)
            {
                _collection.CollectionChanged -= OnCollectionChanged;
                _collection = null;
            }
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            CoalescedActionRequest request = _scrollGate.Request();
            if (!request.ShouldSchedule)
            {
                return;
            }

            _pendingOperation = _listBox.Dispatcher.BeginInvoke(
                () => ScrollToEnd(request.Token),
                DispatcherPriority.Render);
        }

        private void ScrollToEnd(long token)
        {
            _pendingOperation = null;
            if (!_scrollGate.TryBeginExecution(token))
            {
                return;
            }

            try
            {
                if (_isActive && GetIsEnabled(_listBox) && _listBox.Items.Count > 0)
                {
                    _listBox.ScrollIntoView(_listBox.Items[^1]);
                }
            }
            finally
            {
                _scrollGate.Complete(token);
            }
        }

        private void CancelPendingScroll()
        {
            _scrollGate.Cancel();
            _pendingOperation?.Abort();
            _pendingOperation = null;
        }
    }
}
