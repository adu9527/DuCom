using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.Plugins.Audio;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DuCom.Services.Plugins;

public sealed partial class AudioWindow : FluentWindow
{
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<bool>> _invoke;
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<bool>> _import;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private IReadOnlyList<SpeechVoice> _voices = [];
    private int _voiceRevision = -1, _importRevision = -1;
    private bool _initialized, _busy, _hostClosing, _ready, _playing, _playbackEnded;
    private string _selectedVoice = "zh-CN-XiaoxiaoNeural", _lastFile = "", _subtitlePath = "", _loadedFile = "";
    private IReadOnlyList<SubtitleItem> _subtitles = [];

    public AudioWindow(Func<string, IReadOnlyDictionary<string, string>, Task<bool>> invoke, Func<string, IReadOnlyDictionary<string, string>, Task<bool>> import)
    {
        _invoke = invoke; _import = import;
        InitializeComponent();
        Topmost = PluginToolWindowPreferencesService.Load().Topmost;
        PinInput.IsChecked = Topmost;
        _ready = true;
        ApplyTheme(false);
        _player.MediaFailed += (_, e) => { _playing = false; SetStatus("播放失败：" + e.ErrorException.Message, true); };
        _player.MediaEnded += (_, _) =>
        {
            _playing = false; _playbackEnded = true;
            _player.Stop();
            TickPlayback(); UpdatePlayIcon();
        };
        _player.MediaOpened += (_, _) => { PlayerMeta.Text = "MP3 · 24 kHz · 单声道"; TickPlayback(); };
        _playbackTimer.Tick += (_, _) => TickPlayback();
        _playbackTimer.Start();
        Loaded += (_, _) => { if (_voices.Count == 0) _ = RunAsync("voices"); };
        Closing += OnClosing;
        SizeChanged += (_, _) =>
        {
            LeftColumn.Width = new GridLength(ActualWidth <= 1100 ? 280 : 310);
            TextEditor.Height = ActualHeight < 760 ? 180 : 286;
        };
    }

    internal void UpdateState(IReadOnlyList<UiNode> nodes)
    {
        UiTextNode? state = nodes.OfType<UiTextNode>().FirstOrDefault(n => n.FieldId == "native-state");
        if (state is null) return;
        using JsonDocument json = JsonDocument.Parse(state.Text); JsonElement root = json.RootElement;
        _busy = root.GetProperty("Busy").GetBoolean();
        TextEditor.IsReadOnly = _busy;
        GenerateButton.Content = _busy ? "停止合成" : "开始合成";
        PreviewButton.IsEnabled = ClearButton.IsEnabled = !_busy;
        ExportMp3.IsEnabled = ExportSrt.IsEnabled = !_busy;
        ProgressPanel.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        int percent = root.GetProperty("Percent").GetInt32();
        SynthesisProgress.Value = percent; SynthesisProgress.IsIndeterminate = _busy && percent == 0;
        PercentText.Text = percent + "%";
        SetStatus(root.GetProperty("Status").GetString() ?? "", root.GetProperty("Error").GetBoolean());
        ConnectionText.Text = _busy ? "处理中" : root.GetProperty("Error").GetBoolean() ? "连接异常" : _voices.Count > 0 ? "服务在线" : "准备就绪";
        StatusDot.SetResourceReference(ShapeFill, _busy ? "Audio.Primary" : root.GetProperty("Error").GetBoolean() ? "Audio.Error" : "Audio.Green");
        DirectoryHint.Text = root.GetProperty("Directory").GetString(); DirectoryHint.ToolTip = DirectoryHint.Text;
        if (!_initialized)
        {
            AudioPreferences p = root.GetProperty("Preferences").Deserialize<AudioPreferences>() ?? new();
            _selectedVoice = p.Voice; RateInput.Value = p.Rate; PitchInput.Value = p.Pitch; VolumeInput.Value = p.Volume; WordBoundary.IsChecked = p.WordBoundary; SentenceBoundary.IsChecked = !p.WordBoundary;
            _initialized = true;
        }
        int voiceRevision = root.GetProperty("VoiceRevision").GetInt32();
        if (_voiceRevision != voiceRevision)
        {
            _voiceRevision = voiceRevision;
            _voices = nodes.OfType<UiTextNode>().Where(n => n.FieldId == "voice-item").Select(n => JsonSerializer.Deserialize<SpeechVoice>(n.Text)!).ToArray(); FilterVoices();
        }
        int importRevision = root.GetProperty("ImportRevision").GetInt32();
        if (_importRevision != importRevision)
        {
            if (importRevision > 0) TextEditor.Text = string.Concat(nodes.OfType<UiTextNode>().Where(n => n.FieldId == "import-part").Select(n => n.Text));
            _importRevision = importRevision;
        }
        OutputItem[] history = nodes.OfType<UiTextNode>().Where(n => n.FieldId == "output-item").Select(n => new OutputItem(JsonSerializer.Deserialize<AudioOutput>(n.Text)!)).ToArray();
        HistoryList.ItemsSource = history;
        HistoryList.Visibility = history.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmpty.Visibility = history.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCount.Text = history.Length == 0 ? "" : history.Length + " 个文件";
        OutputItem? subtitleOnly = history.FirstOrDefault();
        if (!_busy && subtitleOnly is { Output.Path.Length: 0 } && File.Exists(subtitleOnly.Output.SubtitlePath))
        {
            _subtitlePath = subtitleOnly.Output.SubtitlePath;
            _subtitles = ParseSubtitles(File.ReadAllText(_subtitlePath));
            SubtitleList.ItemsSource = _subtitles; SubtitleCount.Text = _subtitles.Count + " 条";
            SubtitleCard.Visibility = _subtitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _player.Close(); _loadedFile = ""; _playing = false; PlayerCard.Visibility = Visibility.Collapsed;
        }
        string last = root.GetProperty("LastFile").GetString() ?? "";
        if (last.Length > 0 && last != _lastFile)
        {
            _lastFile = last;
            LoadFile(last, history.FirstOrDefault(h => h.Output.Path == last)?.Output.SubtitlePath ?? "", false);
        }
    }
    private static readonly DependencyProperty ShapeFill = System.Windows.Shapes.Shape.FillProperty;
    private void FilterVoices()
    {
        string search = SearchInput.Text.Trim();
        VoiceItem[] items = _voices.Select(v => new VoiceItem(v)).Where(v => search.Length == 0 || v.Search.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        VoiceList.ItemsSource = items;
        VoiceList.SelectedItem = items.FirstOrDefault(v => v.Voice.ShortName == _selectedVoice);
        VoiceCount.Text = $"{items.Length} 个";
        if (_voices.Count > 0 && !_busy) ConnectionText.Text = "服务在线";
        UpdateCurrentVoice();
    }
    private void UpdateCurrentVoice()
    {
        SpeechVoice? voice = _voices.FirstOrDefault(v => v.ShortName == _selectedVoice);
        if (voice is null) return;
        VoiceItem item = new(voice); CurrentVoice.Text = item.Name; CurrentLocale.Text = VoiceItem.Language(voice.Locale);
    }
    private Dictionary<string, string> Values() => new()
    {
        ["text"] = TextEditor.Text, ["voice"] = _selectedVoice, ["proxy"] = "", ["filename"] = FilenameInput.Text,
        ["mp3"] = ExportMp3.IsChecked == true ? "true" : "false", ["srt"] = ExportSrt.IsChecked == true ? "true" : "false",
        ["rate"] = ((int)RateInput.Value).ToString(CultureInfo.InvariantCulture), ["pitch"] = ((int)PitchInput.Value).ToString(CultureInfo.InvariantCulture), ["volume"] = ((int)VolumeInput.Value).ToString(CultureInfo.InvariantCulture), ["boundary"] = WordBoundary.IsChecked == true ? "word" : "sentence",
    };
    private async Task RunAsync(string command)
    {
        if (_busy && command != "stop") return;
        if (command == "generate" && ExportMp3.IsChecked != true && ExportSrt.IsChecked != true)
        { SetStatus("请至少勾选一种输出格式：MP3 或 SRT。", true); return; }
        if (command is "stop" or "sample" or "preview" or "generate") { _player.Stop(); _playing = false; UpdatePlayIcon(); }
        try { if (!await _invoke(command, Values())) SetStatus("请求未能启动，请检查插件是否启用。", true); }
        catch (Exception e) { SetStatus("操作失败：" + e.Message, true); }
    }
    private async void VoicePreview_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: VoiceItem item }) return;
        if (_busy)
        {
            await RunAsync("stop");
            SetStatus("已请求停止，请稍后再次试听。"); return;
        }
        _selectedVoice = item.Voice.ShortName; VoiceList.SelectedItem = item; UpdateCurrentVoice();
        await RunAsync("sample");
    }
    private void Voice_Changed(object sender, SelectionChangedEventArgs e)
    { if (VoiceList.SelectedItem is VoiceItem item) { _selectedVoice = item.Voice.ShortName; UpdateCurrentVoice(); } }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready) FilterVoices(); }
    private void Parameter_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        RateValue.Text = Signed(RateInput.Value) + "%"; PitchValue.Text = Signed(PitchInput.Value) + "Hz"; VolumeValue.Text = Signed(VolumeInput.Value) + "%";
        UpdateTextMeta();
    }
    private static string Signed(double n) => ((int)n).ToString("+0;-0;+0", CultureInfo.InvariantCulture);
    private void Text_Changed(object sender, TextChangedEventArgs e) { if (_ready) UpdateTextMeta(); }
    private void UpdateTextMeta()
    {
        double speed = Math.Max(0.1, 1 + RateInput.Value / 100);
        int seconds = (int)(TextEditor.Text.Length / (4.5 * speed));
        TextMeta.Text = $"字符  {TextEditor.Text.Length:N0}       预计  {seconds / 60}:{seconds % 60:00}";
    }
    private void Reset_Click(object sender, RoutedEventArgs e) => RateInput.Value = PitchInput.Value = VolumeInput.Value = 0;
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync("voices");
    private async void Generate_Click(object sender, RoutedEventArgs e) => await RunAsync(_busy ? "stop" : "generate");
    private async void Preview_Click(object sender, RoutedEventArgs e) => await RunAsync("preview");
    private void Clear_Click(object sender, RoutedEventArgs e) { if (!_busy) TextEditor.Clear(); }
    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync("import");
    private void Text_DragOver(object sender, DragEventArgs e)
    { e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Text_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return;
        if (Path.GetExtension(paths[0]).ToLowerInvariant() is not (".txt" or ".md")) { SetStatus("请拖入一个 TXT 或 MD 文件。", true); return; }
        try { if (!await _import(paths[0], Values())) SetStatus("导入未能启动，请检查插件状态。", true); } catch (Exception ex) { SetStatus(ex.Message, true); }
    }
    private void SetStatus(string message, bool error = false)
    { StatusText.Text = message; StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "Audio.Error" : "Audio.Muted"); }

    private void LoadFile(string path, string subtitle, bool play)
    {
        if (!File.Exists(path)) { SetStatus("文件已不存在，请重新合成。", true); return; }
        try
        {
            _loadedFile = path; _subtitlePath = subtitle;
            _player.Close(); _playbackEnded = false; _player.Open(new Uri(path)); _playing = play;
            if (play) _player.Play();
            PlayerCard.Visibility = Visibility.Visible; PlayerFile.Text = Path.GetFileName(path); PlayerMeta.Text = $"{new FileInfo(path).Length / 1024.0:N1} KiB · MP3";
            SubtitleButton.IsEnabled = File.Exists(subtitle);
            _subtitles = File.Exists(subtitle) ? ParseSubtitles(File.ReadAllText(subtitle)) : [];
            SubtitleList.ItemsSource = _subtitles; SubtitleCount.Text = _subtitles.Count + " 条";
            SubtitleCard.Visibility = _subtitles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdatePlayIcon();
        }
        catch (Exception e) { SetStatus("打开成品失败：" + e.Message, true); }
    }
    private static IReadOnlyList<SubtitleItem> ParseSubtitles(string srt)
    {
        List<SubtitleItem> cues = [];
        foreach (string block in Regex.Split(srt.Replace("\r", ""), @"\n\s*\n"))
        {
            string[] lines = block.Trim().Split('\n');
            if (lines.Length < 3) continue;
            string[] times = lines[1].Split(" --> ");
            if (times.Length != 2 || !TimeSpan.TryParse(times[0].Replace(',', '.'), CultureInfo.InvariantCulture, out TimeSpan start)
                || !TimeSpan.TryParse(times[1].Replace(',', '.'), CultureInfo.InvariantCulture, out TimeSpan end)) continue;
            cues.Add(new(cues.Count + 1, start, end, string.Join("\n", lines.Skip(2))));
        }
        return cues;
    }
    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_loadedFile.Length == 0) return;
        if (_playing) _player.Pause();
        else
        {
            if (_playbackEnded || (_player.NaturalDuration.HasTimeSpan && _player.Position >= _player.NaturalDuration.TimeSpan))
                _player.Position = TimeSpan.Zero;
            _playbackEnded = false;
            _player.Play();
        }
        _playing = !_playing; UpdatePlayIcon();
    }
    private void UpdatePlayIcon() => PlayIcon.Data = Geometry.Parse(_playing ? "M3,2 L8,2 8,22 3,22 Z M13,2 L18,2 18,22 13,22 Z" : "M4,2 L18,12 4,22 Z");
    private void TickPlayback()
    {
        if (!_player.NaturalDuration.HasTimeSpan) return;
        double duration = _player.NaturalDuration.TimeSpan.TotalSeconds;
        if (!SeekInput.IsMouseCaptureWithin) SeekInput.Value = duration > 0 ? _player.Position.TotalSeconds / duration : 0;
        PlaybackVisual.Value = SeekInput.Value * 100;
        PlaybackTime.Text = FormatTime(_player.Position) + " / " + FormatTime(_player.NaturalDuration.TimeSpan);
        SubtitleItem? current = _subtitles.FirstOrDefault(c => _player.Position >= c.Start && _player.Position < c.End);
        if (!ReferenceEquals(current, SubtitleList.SelectedItem)) { SubtitleList.SelectedItem = current; if (current is not null) SubtitleList.ScrollIntoView(current); }
    }
    private static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    private void Seek_Changed(object sender, MouseButtonEventArgs e)
    {
        if (!_player.NaturalDuration.HasTimeSpan) return;
        _playbackEnded = false;
        _player.Position = TimeSpan.FromSeconds(SeekInput.Value * _player.NaturalDuration.TimeSpan.TotalSeconds);
    }
    private void RevealAudio_Click(object sender, RoutedEventArgs e) => Reveal(_loadedFile);
    private void RevealSubtitle_Click(object sender, RoutedEventArgs e) => Reveal(_subtitlePath);
    private void History_Play(object sender, MouseButtonEventArgs e)
    { if (HistoryList.SelectedItem is OutputItem item) { if (item.Output.Path.Length > 0) LoadFile(item.Output.Path, item.Output.SubtitlePath, true); else Reveal(item.Output.SubtitlePath); } }
    private void History_Reveal(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: OutputItem item }) Reveal(item.Output.Path.Length > 0 ? item.Output.Path : item.Output.SubtitlePath); }
    private void Reveal(string path)
    {
        if (!File.Exists(path)) { SetStatus("文件不存在，请先合成音频。", true); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true }); } catch (Exception e) { SetStatus(e.Message, true); }
    }
    private void Pin_Changed(object sender, RoutedEventArgs e) { if (_ready) Topmost = PinInput.IsChecked == true; }
    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool light = LightTheme.IsChecked == true || (SystemTheme.IsChecked == true && SystemParameters.WindowGlassColor.R + SystemParameters.WindowGlassColor.G + SystemParameters.WindowGlassColor.B > 550);
        ApplyTheme(light);
    }
    private void ApplyTheme(bool light)
    {
        string[] keys = ["Bg","Panel","Inset","Raised","Border","Strong","Text","Secondary","Muted","Primary","Accent","Soft","Selected","Hover","Green","Error"];
        string[] colors = light
            ? ["#F4F4F5","#FFFFFF","#FAFAFA","#F4F4F5","#D4D4D8","#A1A1AA","#18181B","#52525B","#60606A","#2563EB","#2563EB","#122563EB","#DBEAFE","#E4E4E7","#047857","#B91C1C"]
            : ["#09090B","#18181B","#121214","#27272A","#3F3F46","#52525B","#F4F4F5","#A1A1AA","#A1A1AA","#60A5FA","#2563EB","#1A60A5FA","#1E3A5F","#3F3F46","#6EE7B7","#F87171"];
        for (int i = 0; i < keys.Length; i++) Resources["Audio." + keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        // FluentWindow's theme service may own the window background; paint our client surface
        // explicitly as well, so a locally selected theme never exposes the host's dark chrome.
        Background = (Brush)Resources["Audio.Bg"];
        Foreground = (Brush)Resources["Audio.Text"];
        // Caption buttons use the shared host tokens; scope them locally to this window's theme.
        Resources["Brush.TextPrimary"] = Resources["Audio.Text"];
        Resources["Brush.ControlHover"] = Resources["Audio.Hover"];
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _playbackTimer.Stop(); _player.Close(); PluginToolWindowPreferencesService.Save(new PluginToolWindowPreferences(Topmost));
        if (!_hostClosing) _ = RunAsync("stop");
    }
    internal void CloseFromHost() { _hostClosing = true; Close(); }
    private sealed record SubtitleItem(int Index, TimeSpan Start, TimeSpan End, string Text)
    { public string Time => FormatTime(Start) + " – " + FormatTime(End); }
    private sealed record OutputItem(AudioOutput Output)
    {
        public string Name => Path.GetFileName(Output.Path.Length > 0 ? Output.Path : Output.SubtitlePath);
        public string Meta => Output.Created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + $" · {Output.Bytes / 1024.0:N1} KiB";
    }
    private sealed record VoiceItem(SpeechVoice Voice)
    {
        public string Name => Voice.ShortName switch { "zh-CN-XiaoxiaoNeural" => "晓晓", "zh-CN-YunxiNeural" => "云希", "zh-CN-YunjianNeural" => "云健", "zh-CN-XiaoyiNeural" => "晓伊", "zh-CN-YunyangNeural" => "云扬", "zh-CN-YunxiaNeural" => "云夏", _ => Voice.ShortName.Split('-').Last().Replace("Neural", "", StringComparison.Ordinal) };
        public string Initial => Name[..1];
        public string Meta => Language(Voice.Locale) + " · " + (Voice.Gender == "Female" ? "女声" : "男声") + " · " + Voice.Locale;
        public string Search => Name + " " + Meta + " " + Voice.ShortName + " " + Voice.FriendlyName;
        public Brush AvatarBrush => new LinearGradientBrush(Voice.Gender == "Female" ? Color.FromRgb(124, 91, 190) : Color.FromRgb(73, 118, 190), Color.FromRgb(74, 102, 176), 45);
        public static string Language(string locale)
        {
            if (locale.StartsWith("zh-", StringComparison.Ordinal)) return locale switch { "zh-CN" => "中文", "zh-HK" => "粤语", "zh-TW" => "台湾中文", _ => "中文 " + locale };
            try { return CultureInfo.GetCultureInfo(locale).DisplayName; } catch (CultureNotFoundException) { return locale; }
        }
    }
}
