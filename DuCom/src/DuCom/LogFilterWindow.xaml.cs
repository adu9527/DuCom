using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DuCom.Behaviors;
using DuCom.Core.Parsing;
using DuCom.Core.Sessions;
using DuCom.ViewModels;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace DuCom;

/// <summary>
/// Per-port real-time log filter window mirroring the reference tool's behavior 1:1: the
/// surface shows only complete lines that contain at least one keyword (keywords split on
/// spaces, commas, semicolons and tabs; case-insensitive). Data comes from the same session
/// display tap stream as the float send window, with its own STR/HEX display switch, fixed
/// scroll, clear, and save-as snapshot. The tap publish callback runs on the receive
/// pipeline thread and only enqueues work.
/// </summary>
public partial class LogFilterWindow : FluentWindow
{
    private const string TapId = "log-filter";
    private const int MaximumBufferCharacters = 2 * 1024 * 1024;
    private const int MaximumLineCount = 5_000;
    private static readonly char[] KeywordSeparators = [' ', ',', ';', '\t', '\uFF0C', '\uFF1B'];

    private readonly SessionViewModel _session;
    private readonly Queue<string> _pendingText = new();
    private readonly object _pendingGate = new();
    private readonly StringBuilder _lineBuffer = new();
    private readonly List<string> _keywords = [];
    private bool _flushScheduled;
    private volatile bool _recvShowHex;
    private int _bufferCharacters;
    private bool _fixedLog;
    private bool _isClosed;

    public LogFilterWindow(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        InitializeComponent();
        DataContext = this;

        _recvShowHex = session.AppliedReceiveMode == ReceiveDisplayMode.Hex;
        RecvHexToggle.IsChecked = _recvShowHex;
        Title = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            (string?)TryFindResource("LogFilter.TitleFormat") ?? "DuCom log filter - {0}",
            session.PortName);
        FilterTitleBar.Title = Title;
        LogList.ItemsSource = Lines;
        UpdateMatchStatus();

        _session.RegisterDisplayTap(new SessionDisplayTap
        {
            Id = TapId,
            FormatSelector = () => _recvShowHex ? SessionTapDisplayFormat.Hex : SessionTapDisplayFormat.Str,
            Publish = EnqueueText,
        });
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    public string PortName => _session.PortName;

    internal SessionViewModel Session => _session;

    public ObservableCollection<TapLine> Lines { get; } = [];

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
            Program.DiagnosticLog?.Warning($"Log filter window flush failed. Port={PortName}; {exception.Message}");
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

        // Only complete lines are eligible; the trailing partial stays buffered like the
        // reference implementation.
        bool added = false;
        int start = 0;
        int newlineIndex;
        while ((newlineIndex = buffer.IndexOf('\n', start)) >= 0)
        {
            string line = buffer[start..(newlineIndex + 1)];
            start = newlineIndex + 1;
            if (LineMatches(line))
            {
                AppendLine(line);
                added = true;
            }
        }

        if (start < buffer.Length)
        {
            _lineBuffer.Append(buffer[start..]);
        }

        if (!added)
        {
            return;
        }

        TrimBuffer();
        if (!_fixedLog)
        {
            LogList.ScrollIntoView(Lines.Count > 0 ? Lines[^1] : null);
        }
    }

    private void AppendLine(string line)
    {
        // Only complete lines reach this point, so each rendered item is one matched line.
        string trimmed = line.TrimEnd('\r', '\n');
        Lines.Add(new TapLine(trimmed));
        _bufferCharacters += trimmed.Length;
        UpdateMatchStatus();
    }

    private void TrimBuffer()
    {
        while (Lines.Count > MaximumLineCount ||
               _bufferCharacters > MaximumBufferCharacters && Lines.Count > 1)
        {
            TapLine first = Lines[0];
            Lines.RemoveAt(0);
            _bufferCharacters -= first.Text.Length;
        }

        UpdateMatchStatus();
    }

    private void UpdateKeywords(string text)
    {
        _keywords.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (string part in text.Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string keyword = part.Trim();
            if (keyword.Length > 0 && !_keywords.Contains(keyword))
            {
                _keywords.Add(keyword);
            }
        }
    }

    private bool LineMatches(string line)
    {
        if (_keywords.Count == 0)
        {
            // Reference behavior: with no keyword configured, nothing is shown.
            return false;
        }

        foreach (string keyword in _keywords)
        {
            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.IsOpen) && !_session.IsOpen)
        {
            // The port session closed: the per-port filter window closes with it.
            Close();
        }
    }

    private void KeywordBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        KeywordPlaceholder.Visibility = KeywordBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateKeywords(KeywordBox.Text);
    }

    private void UpdateMatchStatus()
    {
        EmptyStateText.Visibility = Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MatchStatusText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            (string?)TryFindResource("LogFilter.MatchCount") ?? "{0} matching lines",
            Lines.Count);
    }

    private void RecvHexToggle_Click(object sender, RoutedEventArgs e)
    {
        _recvShowHex = RecvHexToggle.IsChecked == true;
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
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Lines.Clear();
        _bufferCharacters = 0;
        _lineBuffer.Clear();
        lock (_pendingGate)
        {
            _pendingText.Clear();
        }

        UpdateMatchStatus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        string snapshot = string.Join(Environment.NewLine, Lines.Select(line => line.Text));
        if (snapshot.Length == 0)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = (string?)TryFindResource("FloatSend.SaveFilter") ?? "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"DuCom-{PortName}-filter-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
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
            Program.DiagnosticLog?.Warning($"Failed to save filtered log. Port={PortName}; {exception.Message}");
            ThemedMessageDialog.Show(
                this,
                (string?)TryFindResource("LogFilter.SaveFailed") ?? "Failed to save the filtered log.",
                Title,
                ThemedMessageDialogKind.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        _session.UnregisterDisplayTap(TapId);
        LogList.ItemsSource = null;
        if (Application.Current.MainWindow is MainWindow owner)
        {
            owner.NotifyLogFilterClosed(PortName);
        }

        base.OnClosed(e);
    }
}
