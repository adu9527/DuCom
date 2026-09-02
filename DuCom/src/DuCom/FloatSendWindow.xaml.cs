using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuCom.Behaviors;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;
using DuCom.Core.Sessions;
using DuCom.Services;
using DuCom.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DuCom;

/// <summary>
/// Per-port floating send window mirroring the reference tool's mini-log behavior 1:1:
/// an independent log surface fed by a session display tap (own STR/HEX display switch,
/// reply-window format following the last send mode), independent clear and save-as
/// snapshots, fixed-scroll with reply positioning, and the full send bar including
/// command groups run against this port only. The tap publish callback runs on the
/// receive pipeline thread and only enqueues work.
/// </summary>
public partial class FloatSendWindow : FluentWindow
{
    private const string TapId = "float-send";
    private const int MaximumBufferCharacters = 2 * 1024 * 1024;
    private const int MaximumLineCount = 5_000;

    private readonly SessionViewModel _session;
    private MiniLogPreferences _preferences;
    private readonly Queue<string> _pendingText = new();
    private readonly object _pendingGate = new();
    private readonly StringBuilder _lineBuffer = new();
    private bool _flushScheduled;
    private volatile bool _recvShowHex;
    private volatile int _replyWindowMs = FloatSendGlobalPreferencesService.DefaultReplyWindowMs;
    private int _bufferCharacters;
    private bool _fixedLog;
    private TapLine? _tailLine;
    private TapLine? _lastSendAnchor;
    private bool _isClosed;

    public FloatSendWindow(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        InitializeComponent();
        DataContext = this;

        _preferences = MiniLogPreferencesService.Load(PortName);
        FloatSendGlobalPreferences global = FloatSendGlobalPreferencesService.Load();
        ApplyGeometry(_preferences);
        Topmost = global.Topmost;
        TopmostToggle.IsChecked = Topmost;
        PinLogToggle.IsChecked = false;
        Behaviors.ListBoxAutoScrollBehavior.SetIsEnabled(LogList, true);
        SendHexToggle.IsChecked = _preferences.SendMode == SendMode.Hex;
        NewlineToggle.IsChecked = _preferences.Newline == NewlinePolicy.CrLf;
        _recvShowHex = session.AppliedReceiveMode == ReceiveDisplayMode.Hex;
        RecvHexToggle.IsChecked = _recvShowHex;
        _replyWindowMs = global.ReplyWindowMs;
        ReplyWindowBox.Text = _replyWindowMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Title = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            (string?)TryFindResource("FloatSend.TitleFormat") ?? "DuCom float send - {0}",
            session.PortName);
        FloatTitleBar.Title = Title;
        LogList.ItemsSource = Lines;
        SendBox.Text = string.Empty;

        _session.RegisterDisplayTap(new SessionDisplayTap
        {
            Id = TapId,
            FormatSelector = SelectFormat,
            Publish = EnqueueText,
        });
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    public string PortName => _session.PortName;

    public SessionViewModel Session => _session;

    public ObservableCollection<TapLine> Lines { get; } = [];

    private SessionTapDisplayFormat SelectFormat()
    {
        SendMode? replyMode = _session.DisplayTaps.ResolveReplyWindowFormat(_replyWindowMs);
        if (replyMode.HasValue)
        {
            // Reply-window rule: within the window, replies render in the sent mode.
            return replyMode.Value == SendMode.Hex ? SessionTapDisplayFormat.Hex : SessionTapDisplayFormat.Str;
        }

        return _recvShowHex ? SessionTapDisplayFormat.Hex : SessionTapDisplayFormat.Str;
    }

    private void EnqueueText(string text)
    {
        if (string.IsNullOrEmpty(text) || _isClosed)
        {
            return;
        }

        lock (_pendingGate)
        {
            _pendingText.Enqueue(text);
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
        }

        Dispatcher.BeginInvoke(FlushPendingText, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushPendingText()
    {
        string text;
        lock (_pendingGate)
        {
            if (_pendingText.Count == 0)
            {
                _flushScheduled = false;
                return;
            }

            text = string.Concat(_pendingText);
            _pendingText.Clear();
        }

        try
        {
            AppendStreamText(text);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Float send window flush failed. Port={PortName}; {exception.Message}");
        }

        lock (_pendingGate)
        {
            if (_pendingText.Count == 0)
            {
                _flushScheduled = false;
                return;
            }
        }

        Dispatcher.BeginInvoke(FlushPendingText, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AppendStreamText(string text)
    {
        _lineBuffer.Append(text);
        string buffer = _lineBuffer.ToString();
        _lineBuffer.Clear();

        bool added = false;
        int start = 0;
        int newlineIndex;
        while ((newlineIndex = buffer.IndexOf('\n', start)) >= 0)
        {
            string piece = buffer[start..newlineIndex].TrimEnd('\r');
            FinalizeTail(piece);
            added = true;
            start = newlineIndex + 1;
        }

        string remainder = buffer[start..];
        if (remainder.Length > 0)
        {
            ExtendTail(remainder);
            added = true;
        }

        if (!added)
        {
            return;
        }

        TrimBuffer();
        if (_fixedLog && _lastSendAnchor is not null)
        {
            // Fixed-scroll reply positioning: jump back to the line captured at send time.
            TapLine anchor = _lastSendAnchor;
            _lastSendAnchor = null;
            LogList.ScrollIntoView(anchor);
        }
    }

    private void FinalizeTail(string piece)
    {
        if (_tailLine is null)
        {
            Lines.Add(new TapLine(piece));
        }
        else
        {
            _tailLine = new TapLine(_tailLine.Text + piece);
            Lines[^1] = _tailLine;
            _tailLine = null;
            return;
        }

        _bufferCharacters += piece.Length + 2;
    }

    private void ExtendTail(string piece)
    {
        if (_tailLine is null)
        {
            _tailLine = new TapLine(piece);
            Lines.Add(_tailLine);
            _bufferCharacters += piece.Length;
        }
        else
        {
            _tailLine = new TapLine(_tailLine.Text + piece);
            Lines[^1] = _tailLine;
            _bufferCharacters += piece.Length;
        }
    }

    private void TrimBuffer()
    {
        while (Lines.Count > MaximumLineCount ||
               _bufferCharacters > MaximumBufferCharacters && Lines.Count > 1)
        {
            TapLine first = Lines[0];
            Lines.RemoveAt(0);
            _bufferCharacters -= first.Text.Length + 2;
            if (ReferenceEquals(first, _tailLine))
            {
                _tailLine = null;
            }

            if (ReferenceEquals(first, _lastSendAnchor))
            {
                _lastSendAnchor = null;
            }
        }
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsOpen) && !_session.IsOpen)
        {
            // The port session closed: the per-port float window closes with it.
            Close();
        }
    }

    private void RecvHexToggle_Click(object sender, RoutedEventArgs e)
    {
        _recvShowHex = RecvHexToggle.IsChecked == true;
    }

    private void SendHexToggle_Click(object sender, RoutedEventArgs e)
    {
        // Send mode is a per-port preference shared with the main workspace send bar.
        _session.SendMode = SendHexToggle.IsChecked == true ? SendMode.Hex : SendMode.Str;
        SavePreferences();
    }

    private void PinLogToggle_Click(object sender, RoutedEventArgs e)
    {
        _fixedLog = PinLogToggle.IsChecked == true;
        Behaviors.ListBoxAutoScrollBehavior.SetIsEnabled(LogList, !_fixedLog);
        if (!_fixedLog)
        {
            LogList.ScrollIntoView(Lines.Count > 0 ? Lines[^1] : null);
        }
    }

    private void TopmostToggle_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
        FloatSendGlobalPreferencesService.Save(new FloatSendGlobalPreferences(
            ReplyWindowMs: _replyWindowMs,
            Topmost: Topmost));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        // Clears only the float window surface; the main session display is untouched.
        Lines.Clear();
        _tailLine = null;
        _lastSendAnchor = null;
        _bufferCharacters = 0;
        _lineBuffer.Clear();
        lock (_pendingGate)
        {
            _pendingText.Clear();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        string snapshot = string.Join(
            Environment.NewLine,
            Lines.Select(line => line.Text).Where(text => text.Length > 0));
        if (snapshot.Length == 0)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = (string?)TryFindResource("FloatSend.SaveFilter") ?? "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"DuCom-{PortName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, snapshot + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save float send log. Port={PortName}; {exception.Message}");
            ThemedMessageDialog.Show(
                this,
                (string?)TryFindResource("FloatSend.SaveFailed") ?? "Failed to save the float send log.",
                Title,
                ThemedMessageDialogKind.Error);
        }
    }

    private void ReplyWindowBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = ReplyWindowBox.Text;
        foreach (char character in text)
        {
            if (!char.IsDigit(character))
            {
                ReplyWindowBox.Text = new string(text.Where(char.IsDigit).ToArray());
                ReplyWindowBox.CaretIndex = ReplyWindowBox.Text.Length;
                break;
            }
        }
    }

    private void ReplyWindowBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyReplyWindow();
            e.Handled = true;
        }
    }

    private void ReplyWindowBox_LostFocus(object sender, RoutedEventArgs e) => ApplyReplyWindow();

    private void ApplyReplyWindow()
    {
        if (!int.TryParse(ReplyWindowBox.Text, out int milliseconds))
        {
            ReplyWindowBox.Text = _replyWindowMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        milliseconds = Math.Clamp(
            milliseconds,
            FloatSendGlobalPreferencesService.MinimumReplyWindowMs,
            FloatSendGlobalPreferencesService.MaximumReplyWindowMs);
        _replyWindowMs = milliseconds;
        ReplyWindowBox.Text = milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        FloatSendGlobalPreferencesService.Save(new FloatSendGlobalPreferences(
            ReplyWindowMs: milliseconds,
            Topmost: Topmost));
        if (Application.Current.MainWindow is MainWindow owner)
        {
            owner.ApplyReplyWindowToFloatSends(this, milliseconds);
        }
    }

    internal void SetReplyWindowMs(int milliseconds)
    {
        _replyWindowMs = milliseconds;
        if (ReplyWindowBox.Text != milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            ReplyWindowBox.Text = milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void GroupsBox_DropOpened(object sender, EventArgs e)
    {
        // Re-read the command store so groups edited in the tool center show up immediately.
        Session.RefreshCommandGroups();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await DoSendAsync();

    private async void SendBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            await DoSendAsync();
            e.Handled = true;
        }
    }

    private async Task DoSendAsync()
    {
        string text = SendBox.Text;
        if (!_session.IsOpen || string.IsNullOrEmpty(text))
        {
            return;
        }

        // Fixed-scroll: remember the current last line so the reply jumps back to it.
        if (_fixedLog && Lines.Count > 0)
        {
            _lastSendAnchor = Lines[^1];
        }

        SendMode mode = SendHexToggle.IsChecked == true ? SendMode.Hex : SendMode.Str;
        NewlinePolicy newline = NewlineToggle.IsChecked == true ? NewlinePolicy.CrLf : NewlinePolicy.None;
        _session.SendMode = mode;
        _session.Newline = newline;
        try
        {
            await _session.WorkspaceSession.SendAsync(mode, text, newline).AsTask();
            SavePreferences();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Float send failed. Port={PortName}; {exception.Message}");
            ThemedMessageDialog.Show(
                this,
                (string?)TryFindResource("FloatSend.SendFailed") ?? "Send failed.",
                Title,
                ThemedMessageDialogKind.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        SavePreferences();
        LogList.ItemsSource = null;
        // The owner may already be closing; unregister directly without asking it to
        // perform another window operation during this window's close notification.
        if (Application.Current.MainWindow?.DataContext is MainViewModel viewModel)
        {
            viewModel.FloatSendClosedFromWindow(PortName);
        }

        _ = DisposeAsync().AsTask();
        base.OnClosed(e);
    }

    /// <summary>Releases the tap subscription; the command host lives on the session view model. Safe to repeat.</summary>
    public async ValueTask DisposeAsync()
    {
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _session.UnregisterDisplayTap(TapId);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        // Non-blocking: the async path releases the tap subscription in the background.
        _ = DisposeAsync().AsTask();
    }

    private void ApplyGeometry(MiniLogPreferences preferences)
    {
        Width = Math.Max(MinWidth, preferences.Width);
        Height = Math.Max(MinHeight, preferences.Height);
        if (preferences.Left is not double left || preferences.Top is not double top)
        {
            return;
        }

        double clampedLeft = Math.Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - MinWidth);
        double clampedTop = Math.Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - MinHeight);
        Left = clampedLeft;
        Top = clampedTop;
    }

    private void SavePreferences()
    {
        SendMode sendMode = SendHexToggle.IsChecked == true ? SendMode.Hex : SendMode.Str;
        NewlinePolicy newline = NewlineToggle.IsChecked == true ? NewlinePolicy.CrLf : NewlinePolicy.None;
        _preferences = new MiniLogPreferences(
            FiniteOrNull(RestoreBounds.Left),
            FiniteOrNull(RestoreBounds.Top),
            FiniteOrDefault(RestoreBounds.Width, Width),
            FiniteOrDefault(RestoreBounds.Height, Height),
            Topmost,
            PinLogToggle.IsChecked != true,
            sendMode,
            newline);
        MiniLogPreferencesService.Save(PortName, _preferences);
    }

    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;

    private static double FiniteOrDefault(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;
}

/// <summary>One rendered line of the float surface; replaced (never mutated) as it grows.</summary>
public sealed record TapLine(string Text);
