namespace DuCom.Core.Presenting;

/// <summary>
/// UI-free registry that owns at most one auxiliary window handle per port name.
/// Ports are compared case-insensitively. All window operations are delegated through
/// callbacks so the registry logic stays testable without WPF.
/// </summary>
public sealed class PortWindowRegistry<TWindow> where TWindow : class
{
    private readonly Dictionary<string, TWindow> _windowsByPort = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<TWindow> _activate;
    private readonly Action<TWindow> _close;

    public PortWindowRegistry(Action<TWindow> activate, Action<TWindow> close)
    {
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
        _close = close ?? throw new ArgumentNullException(nameof(close));
    }

    public int Count => _windowsByPort.Count;

    /// <summary>Snapshot of the currently registered window handles.</summary>
    public IReadOnlyList<TWindow> Windows => [.. _windowsByPort.Values];

    public bool IsOpen(string portName) => _windowsByPort.ContainsKey(portName);

    /// <summary>Returns the existing window for the port (activated), or creates one via the factory.</summary>
    public TWindow GetOrOpen(string portName, Func<string, TWindow> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (_windowsByPort.TryGetValue(portName, out TWindow? existing))
        {
            _activate(existing);
            return existing;
        }

        TWindow created = factory(portName);
        _windowsByPort[portName] = created;
        return created;
    }

    /// <summary>Closes and removes the window for a port. Returns true when a window was registered.</summary>
    public bool Close(string portName)
    {
        if (!_windowsByPort.TryGetValue(portName, out TWindow? window))
        {
            return false;
        }

        _windowsByPort.Remove(portName);
        CloseQuietly(window);
        return true;
    }

    /// <summary>Closes and removes every registered window.</summary>
    public void CloseAll()
    {
        TWindow[] windows = [.. _windowsByPort.Values];
        _windowsByPort.Clear();
        foreach (TWindow window in windows)
        {
            CloseQuietly(window);
        }
    }

    /// <summary>Removes a registration without closing it, for windows the user already closed.</summary>
    public bool Remove(string portName) => _windowsByPort.Remove(portName);

    private void CloseQuietly(TWindow window)
    {
        try
        {
            _close(window);
        }
        catch (Exception)
        {
            // A window that fails to close must not stop the remaining cleanup.
        }
    }
}
