using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DuCom.Services;

/// <summary>One icon policy for every WPF top-level window, including dynamically created tools.</summary>
internal static class TaskbarIconService
{
    internal const string PortableAppId = "DuCom.Portable";
    private static readonly Uri IconUri = new("pack://application:,,,/DuCom;component/Assets/DuCom.ico");
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, WindowIcons> Windows = new();
    private static byte[]? _iconBytes;
    private static ImageSource? _image;
    private static bool _initialized;

    internal static void Initialize(bool portable)
    {
        if (_initialized) return;
        _initialized = true;
        LoadIcon();
        // Installed builds keep the AppID supplied by Velopack and its shortcuts.
        if (portable)
        {
            int result = SetCurrentProcessExplicitAppUserModelID(PortableAppId);
            if (result < 0) Program.DiagnosticLog?.Warning($"Taskbar AppID registration failed: 0x{result:X8}");
        }
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded), handledEventsToo: true);
    }

    private static void LoadIcon()
    {
        if (_iconBytes is not null) return;
        using Stream stream = Application.GetResourceStream(IconUri)!.Stream;
        using MemoryStream copy = new(); stream.CopyTo(copy); _iconBytes = copy.ToArray();
        using MemoryStream imageStream = new(_iconBytes);
        _image = BitmapFrame.Create(imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        _image.Freeze();
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) Attach(window);
    }

    internal static void Attach(Window window)
    {
        if (Windows.TryGetValue(window, out _)) return;
        try
        {
            LoadIcon();
            window.Icon ??= _image;
            WindowIcons icons = new(window, _iconBytes!);
            Windows.Add(window, icons);
        }
        catch (Exception exception)
        { Program.DiagnosticLog?.Warning("Window icon initialization failed: " + window.GetType().Name, exception); }
    }

    private sealed class WindowIcons
    {
        private readonly Window _window;
        private readonly byte[] _bytes;
        private readonly HwndSource? _source;
        private nint _small, _large;
        private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarButtonCreated");
        internal WindowIcons(Window window, byte[] bytes)
        {
            _window = window; _bytes = bytes;
            nint hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0) hwnd = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(WindowMessage);
            Apply(hwnd);
            window.Closed += OnClosed;
        }
        private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if ((uint)message == _taskbarCreated || message == 0x02E0) // taskbar button creation / DPI change
                _window.Dispatcher.BeginInvoke(() => { if (!_window.Dispatcher.HasShutdownStarted && _source is { IsDisposed: false }) Apply(hwnd); });
            return 0;
        }
        private void Apply(nint hwnd)
        {
            uint dpi = GetDpiForWindow(hwnd);
            int smallSize = GetSystemMetricsForDpi(49, dpi); // SM_CXSMICON
            int largeSize = GetSystemMetricsForDpi(11, dpi); // SM_CXICON
            nint small = CreateIcon(_bytes, Math.Max(16, smallSize));
            nint large = CreateIcon(_bytes, Math.Max(32, largeSize));
            if (small == 0 || large == 0)
            {
                if (small != 0) DestroyIcon(small);
                if (large != 0) DestroyIcon(large);
                Program.DiagnosticLog?.Warning("Native window icon decoding failed: " + _window.GetType().Name);
                return;
            }
            SendMessage(hwnd, 0x0080, 0, small); // WM_SETICON / ICON_SMALL
            SendMessage(hwnd, 0x0080, 1, large); // ICON_BIG
            nint oldSmall = _small, oldLarge = _large; _small = small; _large = large;
            if (oldSmall != 0) DestroyIcon(oldSmall);
            if (oldLarge != 0) DestroyIcon(oldLarge);
        }
        private void OnClosed(object? sender, EventArgs e)
        {
            _window.Closed -= OnClosed;
            _source?.RemoveHook(WindowMessage);
            if (_small != 0) DestroyIcon(_small);
            if (_large != 0) DestroyIcon(_large);
            _small = _large = 0;
        }
    }

    private static nint CreateIcon(byte[] ico, int size)
    {
        int count = BitConverter.ToUInt16(ico, 4), bestOffset = -1, bestDistance = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            int width = ico[entry] == 0 ? 256 : ico[entry];
            int distance = Math.Abs(width - size) * 2 + (width < size ? 1 : 0);
            if (distance < bestDistance) { bestDistance = distance; bestOffset = entry; }
        }
        if (bestOffset < 0) return 0;
        int length = BitConverter.ToInt32(ico, bestOffset + 8), offset = BitConverter.ToInt32(ico, bestOffset + 12);
        byte[] image = ico.AsSpan(offset, length).ToArray();
        return CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, size, size, 0);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreateIconFromResourceEx(byte[] bytes, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
}
