using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DuCom.ViewModels;

/// <summary>Coalesces one render-frame mutation batch into a single collection reset.</summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private int _updateDepth;
    private bool _changed;

    public IDisposable BeginUpdate()
    {
        _updateDepth++;
        return new UpdateScope(this);
    }

    public void RemoveFirst(int count)
    {
        int removeCount = Math.Min(count, Count);
        if (removeCount <= 0)
        {
            return;
        }

        if (Items is List<T> list)
        {
            list.RemoveRange(0, removeCount);
        }
        else
        {
            for (int index = removeCount - 1; index >= 0; index--)
            {
                Items.RemoveAt(index);
            }
        }

        MarkChanged();
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_updateDepth > 0)
        {
            _changed = true;
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_updateDepth > 0)
        {
            _changed = true;
            return;
        }

        base.OnPropertyChanged(e);
    }

    private void MarkChanged()
    {
        if (_updateDepth > 0)
        {
            _changed = true;
            return;
        }

        RaiseReset();
    }

    private void EndUpdate()
    {
        _updateDepth--;
        if (_updateDepth == 0 && _changed)
        {
            _changed = false;
            RaiseReset();
        }
    }

    private void RaiseReset()
    {
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private sealed class UpdateScope(BatchObservableCollection<T> owner) : IDisposable
    {
        private BatchObservableCollection<T>? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndUpdate();
    }
}
