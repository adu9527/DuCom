using System.Globalization;
using System.Text;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.Audio;

public sealed class Plugin : DuComPlugin, IDisposable
{
    private AudioPreferences _preferences = new();
    private string _status = "选择语音并输入文本；首次使用请加载在线语音。";
    private string _directory = "", _importedText = "", _lastFile = "";
    private int _importRevision, _voiceRevision, _percent;
    private bool _busy, _error;
    private IReadOnlyList<SpeechVoice> _voices = [];
    private readonly List<AudioOutput> _history = [];
    private string? _taskId;
    private Task? _running;
    private CancellationTokenSource? _runCancellation;
    private readonly SemaphoreSlim _publish = new(1, 1);

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        CancellationToken ct = cancellationToken;
        string? json = await Api.Storage.ReadAsync(ct);
        if (json is not null)
            try { _preferences = JsonSerializer.Deserialize<AudioPreferences>(json) ?? new(); }
            catch (JsonException) { _status = "配置无法读取，已使用默认参数。"; }
    }
    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken) => Task.FromResult(new PluginActivation
    {
        Menus = [new() { ContributionId = "open-audio", Label = "语音合成", CommandId = "open", PageId = "audio", Order = 140 }],
        ToolPages = [new() { ContributionId = "audio", Title = "语音合成", PreferredWidth = 1080, PreferredHeight = 780, MinWidth = 780, Nodes = Nodes() }],
    });
    public override async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        CancellationToken ct = cancellationToken;
        _runCancellation?.Cancel();
        if (_running is not null) try { await _running.WaitAsync(TimeSpan.FromSeconds(1), ct); } catch (Exception) { }
    }

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        string command = commandId;
        IReadOnlyDictionary<string, string> values = formValues;
        CancellationToken ct = cancellationToken;
        if (command is "stop" or "close")
        {
            _runCancellation?.Cancel();
            try { await Api.Speech.StopAsync(ct); } catch (PluginHostException) { }
            if (command == "stop") { _status = "正在停止…"; await Push(ct); }
            return CommandInvokeOutcome.Complete();
        }
        if (_busy) return CommandInvokeOutcome.Reject("请先停止当前任务。");
        if (command == "open")
        {
            _directory = string.IsNullOrEmpty(_preferences.Directory) ? (await Api.Files.GetHostPathsAsync(ct)).LogDirectory : _preferences.Directory;
            await Push(ct); return CommandInvokeOutcome.Complete();
        }
        SpeechRequest request = Parse(values);
        _preferences = _preferences with { Voice = request.Voice, Rate = request.Rate, Pitch = request.Pitch, Volume = request.Volume, WordBoundary = request.WordBoundary, Proxy = request.Proxy };
        _error = false; _busy = true; _percent = 0;
        _status = command == "voices" ? "正在连接微软并加载语音列表…" : command is "preview" or "sample" ? "正在在线生成试听…" : "正在处理…";
        _runCancellation?.Dispose(); _runCancellation = new();
        CancellationToken token = _runCancellation.Token;
        _taskId = "audio-" + Guid.NewGuid().ToString("N");
        string taskId = _taskId;
        StartTask(taskId, context =>
        {
            _running = RunAsync(command, request, values, context, token);
            return _running;
        });
        await Push(ct);
        return CommandInvokeOutcome.Accept(taskId);
    }

    private async Task RunAsync(string command, SpeechRequest request, IReadOnlyDictionary<string, string> values, PluginTaskContext context, CancellationToken userToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(userToken, context.Token);
        CancellationToken ct = linked.Token;
        try
        {
            await Api.Storage.WriteAsync(JsonSerializer.Serialize(_preferences), ct);
            if (command == "voices")
            {
                _voices = await Api.Speech.GetVoicesAsync(request.Proxy, ct);
                _voiceRevision++;
                _status = $"已加载 {_voices.Count} 个在线语音。";
            }
            else if (command is "sample" or "preview")
            {
                string text = command == "sample" ? Sample(request.Voice) : request.Text.Trim();
                if (text.Length == 0) throw new ArgumentException("请先输入试听文本。");
                if (text.Length > 80) text = text[..(char.IsHighSurrogate(text[79]) ? 79 : 80)];
                await Api.Speech.PreviewAsync(request with { Text = text }, ct);
                _status = "试听已开始播放；重复试听相同内容会复用本次会话缓存。";
            }
            else if (command == "directory")
            {
                FilesPickResult? picked = await Api.Files.PickWriteAsync(FilePickWriteOptions.Directory(), ct);
                if (picked is not null)
                {
                    _directory = picked.DisplayPath; _preferences = _preferences with { Directory = _directory };
                    await Api.Storage.WriteAsync(JsonSerializer.Serialize(_preferences), ct);
                    _status = "输出目录已更新。";
                }
            }
            else if (command == "default-directory")
            {
                _preferences = _preferences with { Directory = "" };
                _directory = (await Api.Files.GetHostPathsAsync(ct)).LogDirectory;
                await Api.Storage.WriteAsync(JsonSerializer.Serialize(_preferences), ct);
                _status = "输出目录已恢复为 DuCom 当前日志目录。";
            }
            else if (command == "import") await ImportAsync(ct);
            else if (command == "generate") await GenerateAsync(request, values.GetValueOrDefault("filename", ""), values.GetValueOrDefault("mp3", "true") == "true", values.GetValueOrDefault("srt", "true") == "true", context, ct);
            else _status = "就绪。";
        }
        catch (OperationCanceledException) { _status = "已停止；未完成的合成不会发布为成品文件。"; }
        catch (PluginHostException e) when (e.Code == PluginErrorCode.Cancelled) { _status = e.Message; }
        catch (Exception e) { _error = true; _status = e.Message; Api.Diagnostics.Warning("语音合成：" + e.Message); }
        finally
        {
            _busy = false; _taskId = null;
            try { await Push(CancellationToken.None); } catch (PluginHostException) { }
        }
    }

    private async Task GenerateAsync(SpeechRequest request, string filename, bool exportMp3, bool exportSrt, PluginTaskContext context, CancellationToken ct)
    {
        if (!exportMp3 && !exportSrt) throw new ArgumentException("请至少勾选一种输出格式：MP3 或 SRT。");
        _lastFile = "";
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("请输入需要合成的文本。");
        if (request.Text.Length > AudioText.MaximumTextLength) throw new ArgumentException("文本最多 40,000 个字符，请分批合成。");
        IReadOnlyList<string> chunks = AudioText.Split(request.Text);
        OutputHandle? output = exportMp3 ? await Api.Output.BeginAsync(ct) : null;
        bool committed = false;
        string audioPath = "";
        try
        {
            long bytes = 0;
            List<SpeechCue> cues = [];
            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                _status = $"正在合成 {i + 1}/{chunks.Count} 块 · 已接收 {bytes / 1024.0:N1} KiB";
                _percent = i * 100 / chunks.Count;
                context.ReportProgress(_percent, _status, bytes);
                await Push(ct);
                SpeechResult result = await Api.Speech.SynthesizeChunkAsync(request with { Text = chunks[i] }, ct);
                ct.ThrowIfCancellationRequested();
                byte[] audio = Convert.FromBase64String(result.AudioBase64);
                long compensation = bytes * 8 * 10_000_000 / 48_000;
                cues.AddRange(result.Cues.Select(c => c with { Offset = c.Offset + compensation }));
                if (cues.Count > 100_000) throw new InvalidDataException("字幕过多，请缩短文本。");
                if (bytes + audio.Length > Math.Min(output?.MaxBytes ?? 32 * 1024 * 1024, 32 * 1024 * 1024)) throw new IOException("音频超过 32 MiB 限制，请分批合成。");
                if (output is not null) await Api.Output.WriteAsync(output.Token, bytes, audio, ct);
                bytes += audio.Length;
            }
            ct.ThrowIfCancellationRequested();
            string name = AudioText.SafeName(filename);
            if (output is not null)
            {
                FilesPickResult target = await Api.Files.CreateWriteTargetAsync(_preferences.Directory, name + ".mp3", ct) ?? throw new IOException("无法创建输出目标。");
                ct.ThrowIfCancellationRequested();
                FilesCommitResult saved = await Api.Output.CommitAsync(output.Token, target.Token, Guid.NewGuid().ToString("N"), CancellationToken.None);
                committed = true; audioPath = _lastFile = saved.FinalPath;
            }
            string srt = AudioText.ToSrt(cues);
            string subtitlePath = "";
            if (exportSrt && srt.Length > 0)
            {
                string subtitleName = (audioPath.Length > 0 ? Path.GetFileNameWithoutExtension(audioPath) : name) + ".srt";
                FilesPickResult subtitle = await Api.Files.CreateWriteTargetAsync(_preferences.Directory, subtitleName, ct) ?? throw new IOException("无法创建字幕目标。");
                subtitlePath = await SaveAsync(subtitle.Token, Encoding.UTF8.GetBytes(srt), ct);
            }
            if (exportSrt && srt.Length == 0 && !exportMp3) throw new IOException("此语音未返回字幕，未生成 SRT 文件。请切换语音后重试。");
            _history.Insert(0, new(audioPath, subtitlePath, exportMp3 ? bytes : Encoding.UTF8.GetByteCount(srt), DateTime.Now));
            if (_history.Count > 30) _history.RemoveAt(_history.Count - 1);
            _percent = 100;
            _status = "合成完成 · " + (exportMp3 ? "MP3" : "") + (exportSrt && srt.Length > 0 ? (exportMp3 ? " + SRT" : "SRT") : "") + (exportSrt && srt.Length == 0 ? "（此语音未返回字幕）" : "");
        }
        catch (Exception e) when (committed)
        {
            _history.Insert(0, new(audioPath, "", 0, DateTime.Now));
            throw new IOException("MP3 已保存到 " + audioPath + "，但字幕未完成：" + e.Message, e);
        }
        finally { if (output is not null && !committed) try { await Api.Output.DiscardAsync(output.Token, CancellationToken.None); } catch (PluginHostException) { } }
    }

    private async Task<string> SaveAsync(string target, byte[] data, CancellationToken ct)
    {
        OutputHandle output = await Api.Output.BeginAsync(ct);
        bool saved = false;
        try
        {
            for (int offset = 0; offset < data.Length; offset += output.ChunkMaxBytes)
                await Api.Output.WriteAsync(output.Token, offset, data.AsMemory(offset, Math.Min(output.ChunkMaxBytes, data.Length - offset)), ct);
            FilesCommitResult result = await Api.Output.CommitAsync(output.Token, target, Guid.NewGuid().ToString("N"), ct);
            saved = true; return result.FinalPath;
        }
        finally { if (!saved) try { await Api.Output.DiscardAsync(output.Token, CancellationToken.None); } catch (PluginHostException) { } }
    }

    private async Task ImportAsync(CancellationToken ct)
    {
        FilesPickResult? picked = await Api.Files.PickReadAsync(new FilePickReadOptions(FilterName: "文本 / Markdown", Extensions: ["txt", "md"]), ct);
        if (picked is null) return;
        FilesStatResult before = await Api.Files.StatAsync(picked.Token, ct);
        if (!before.Exists || before.Length > 5 * 1024 * 1024) throw new IOException("请选择不超过 5 MiB 的 TXT / MD 文件。");
        using MemoryStream buffer = new();
        while (buffer.Length < before.Length)
        {
            FileReadChunk chunk = await Api.Files.ReadChunkAsync(picked.Token, buffer.Length, (int)Math.Min(128 * 1024, before.Length - buffer.Length), ct);
            if (chunk.Data.Length == 0 || buffer.Length + chunk.Data.Length > before.Length) throw new IOException("文件长度发生变化，请重新导入。");
            buffer.Write(chunk.Data);
        }
        FilesStatResult after = await Api.Files.StatAsync(picked.Token, ct);
        if (before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc) throw new IOException("文件在读取期间被修改，请重新导入。");
        buffer.Position = 0;
        using StreamReader reader = new(buffer, new UTF8Encoding(false, true), true);
        string text = await reader.ReadToEndAsync(ct);
        if (text.Length > AudioText.MaximumTextLength) throw new IOException("文本超过 40,000 字符，请拆分文件后导入。");
        _importedText = text; _importRevision++; _status = $"已导入 {text.Length:N0} 个字符。";
    }

    private static SpeechRequest Parse(IReadOnlyDictionary<string, string> values) => new()
    {
        Text = values.GetValueOrDefault("text", ""), Voice = values.GetValueOrDefault("voice", "zh-CN-XiaoxiaoNeural"),
        Proxy = values.GetValueOrDefault("proxy", "").Trim(), WordBoundary = values.GetValueOrDefault("boundary") == "word",
        Rate = NumberValue(values.GetValueOrDefault("rate"), -100, 200), Volume = NumberValue(values.GetValueOrDefault("volume"), -100, 100), Pitch = NumberValue(values.GetValueOrDefault("pitch"), -50, 50),
    };
    private static int NumberValue(string? value, int min, int max) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, min, max) : 0;
    private static string Sample(string voice) => voice.StartsWith("zh-", StringComparison.Ordinal) ? "你好，这是我的声音，欢迎使用 DuCom 语音合成。" : voice.StartsWith("ja-", StringComparison.Ordinal) ? "こんにちは。音声合成へようこそ。" : voice.StartsWith("ko-", StringComparison.Ordinal) ? "안녕하세요. 음성 합성에 오신 것을 환영합니다." : voice.StartsWith("de-", StringComparison.Ordinal) ? "Hallo, das ist meine Stimme. Willkommen." : voice.StartsWith("fr-", StringComparison.Ordinal) ? "Bonjour, voici ma voix. Bienvenue." : voice.StartsWith("es-", StringComparison.Ordinal) ? "Hola, esta es mi voz. Bienvenido." : "Hello, this is my voice. Welcome to speech synthesis.";
    private async Task Push(CancellationToken ct)
    {
        await _publish.WaitAsync(ct);
        try { await Api.Ui.UpdateToolPageAsync("audio", Nodes(), ct); }
        finally { _publish.Release(); }
    }
    private IReadOnlyList<UiNode> Nodes()
    {
        List<UiNode> nodes = [new UiTextNode { FieldId = "native-state", ReadOnly = true, Text = JsonSerializer.Serialize(new
        {
            Status = _status, Error = _error, Busy = _busy, Percent = _percent, Directory = _directory, LastFile = _lastFile,
            Preferences = _preferences, ImportRevision = _importRevision, VoiceRevision = _voiceRevision,
        }) }];
        foreach (SpeechVoice voice in _voices) nodes.Add(new UiTextNode { FieldId = "voice-item", ReadOnly = true, Text = JsonSerializer.Serialize(voice) });
        for (int i = 0; i < _importedText.Length; i += 3000) nodes.Add(new UiTextNode { FieldId = "import-part", ReadOnly = true, Text = _importedText.Substring(i, Math.Min(3000, _importedText.Length - i)) });
        foreach (AudioOutput item in _history) nodes.Add(new UiTextNode { FieldId = "output-item", ReadOnly = true, Text = JsonSerializer.Serialize(item) });
        return nodes;
    }
    public void Dispose()
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _publish.Dispose();
    }
}

public sealed record AudioPreferences
{
    public string Voice { get; init; } = "zh-CN-XiaoxiaoNeural";
    public int Rate { get; init; }
    public int Pitch { get; init; }
    public int Volume { get; init; }
    public bool WordBoundary { get; init; }
    public string Proxy { get; init; } = "";
    public string Directory { get; init; } = "";
}
public sealed record AudioOutput(string Path, string SubtitlePath, long Bytes, DateTime Created);
