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

}

/// <summary>One rendered line of the float surface; replaced (never mutated) as it grows.</summary>
public sealed record TapLine(string Text);
