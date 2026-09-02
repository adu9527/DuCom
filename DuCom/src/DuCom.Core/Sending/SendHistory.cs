using System.Text.Json;
using System.Text.Json.Serialization;

namespace DuCom.Core.Sending;

/// <summary>
/// Send-history entries with immediate-duplicate suppression and a fixed capacity.
/// Pure data type; persistence shape is versioned JSON.
/// </summary>
public sealed class SendHistory
{
    private const int DefaultCapacity = 100;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int Capacity { get; }

    private readonly List<string> _entries = [];

    public SendHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>Newest entry is last.</summary>
    public IReadOnlyList<string> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>
    /// Records an entry. Empty/whitespace-only payloads are ignored; an exact duplicate of the
    /// newest entry is ignored; older duplicates are moved to the newest position. Returns true
    /// when the stored list changed.
    /// </summary>
    public bool Record(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        string trimmed = payload.TrimEnd('\r', '\n');
        if (_entries.Count > 0 &&
            string.Equals(_entries[^1], trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        _entries.RemoveAll(entry => string.Equals(entry, trimmed, StringComparison.Ordinal));
        _entries.Add(trimmed);
        while (_entries.Count > Capacity)
        {
            _entries.RemoveAt(0);
        }

        return true;
    }

    public void Clear() => _entries.Clear();

    /// <summary>Returns matching entries newest-first without mutating navigation order.</summary>
    public IReadOnlyList<string> Search(string? query)
    {
        IEnumerable<string> newestFirst = _entries.AsEnumerable().Reverse();
        if (!string.IsNullOrWhiteSpace(query))
        {
            newestFirst = newestFirst.Where(entry => entry.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return [.. newestFirst];
    }

    public void Replace(IEnumerable<string> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        _entries.Clear();
        foreach (string payload in payloads)
        {
            Record(payload);
        }
    }
}

/// <summary>
/// Cursor over <see cref="SendHistory"/> for interactive up/down navigation while preserving
/// the unsubmitted draft. Navigation never mutates the history itself.
/// </summary>
public sealed class SendHistoryNavigator(SendHistory history)
{
    private readonly SendHistory _history = history ?? throw new ArgumentNullException(nameof(history));
    private int _position; // _history.Count means "beyond newest" i.e. draft state
    private string? _draft;

    public bool IsBrowsing => _draft is not null;

    /// <summary>Moves to the previous (older) entry, saving the current text as the draft first.</summary>
    public string? MovePrevious(string currentText)
    {
        if (!IsBrowsing)
        {
            if (_history.Count == 0)
            {
                return null;
            }

            _draft = currentText;
            _position = _history.Count - 1;
            return _history.Entries[_position];
        }

        if (_position <= 0)
        {
            return null;
        }

        _position--;
        return _history.Entries[_position];
    }

    /// <summary>
    /// Moves toward newer entries; stepping beyond the newest restores the saved draft.
    /// Returns null when there is nothing newer than the current position.
    /// </summary>
    public string? MoveNext()
    {
        if (!IsBrowsing)
        {
            return null;
        }

        if (_position >= _history.Count - 1)
        {
            _position = _history.Count;
            string draft = _draft!;
            _draft = null;
            return draft;
        }

        _position++;
        return _history.Entries[_position];
    }

    public void Reset()
    {
        _position = _history.Count;
        _draft = null;
    }
}
