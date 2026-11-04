using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginBroker
{
    private readonly EdgeSpeechClient _speech = new();
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly CancellationTokenSource _speechLifetime = new();
    private CancellationTokenSource? _speechOperation;
    private readonly object _speechCancellationGate = new();
    private string? _previewKey;
    private byte[]? _previewAudio;
    private IReadOnlyList<SpeechVoice>? _speechVoices;
    private string? _voicesProxy;
    private DateTimeOffset _voicesFetched;

    private async Task<JsonElement?> ExecuteSpeechAsync(string operation, JsonElement? data, CancellationToken ct)
    {
        RequirePermission(Permission.SpeechOnline);
        if (operation == "speech.stop")
        {
            lock (_speechCancellationGate) _speechOperation?.Cancel();
            await _environment.PlaySpeechPreviewAsync(_scope.Manifest.Id, null, ct).ConfigureAwait(false);
            return Json(new { ok = true });
        }
        if (!await _speechGate.WaitAsync(0, ct).ConfigureAwait(false)) throw new PluginScopeException(PluginErrorCode.ResourceLimit, "已有语音请求正在执行，请先停止。");
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _speechLifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(95));
        lock (_speechCancellationGate) _speechOperation = linked;
        try
        {
            if (operation == "speech.voices")
            {
                string proxy = data?.GetProperty("Proxy").GetString() ?? "";
                if (_speechVoices is null || proxy != _voicesProxy || DateTimeOffset.UtcNow - _voicesFetched > TimeSpan.FromHours(1))
                {
                    _speechVoices = await _speech.GetVoicesAsync(proxy, linked.Token).ConfigureAwait(false);
                    _voicesProxy = proxy; _voicesFetched = DateTimeOffset.UtcNow;
                }
                _scope.ThrowIfRevoked();
                return Json(_speechVoices);
            }
            SpeechRequest request = Require<SpeechRequest>(data);
            EdgeSpeechClient.Validate(request);
            if (operation == "speech.preview")
            {
                if (request.Text.Length > 80) throw new ArgumentException("试听文本最多 80 个字符。");
                string key = JsonSerializer.Serialize(request);
                if (_previewKey != key || _previewAudio is null)
                {
                    SpeechResult result = await _speech.SynthesizeAsync(request, linked.Token).ConfigureAwait(false);
                    _previewAudio = Convert.FromBase64String(result.AudioBase64); _previewKey = key;
                }
                linked.Token.ThrowIfCancellationRequested(); _scope.ThrowIfRevoked();
                await _environment.PlaySpeechPreviewAsync(_scope.Manifest.Id, _previewAudio, linked.Token).ConfigureAwait(false);
                return Json(new { ok = true });
            }
            SpeechResult chunk = await _speech.SynthesizeAsync(request, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested(); _scope.ThrowIfRevoked();
            return Json(chunk);
        }
        catch (OperationCanceledException)
        {
            throw new PluginScopeException(PluginErrorCode.Cancelled, "请求已停止或网络超时；可检查网络、系统代理后重试。每次连接最多等待 15 秒。");
        }
        catch (Exception e) when (e is not PluginScopeException)
        {
            string detail = e is ArgumentException or InvalidDataException ? e.Message : "无法连接微软语音服务。请检查网络或代理；403 表示服务认证/访问被拒绝，429 表示请求过多，请稍后重试。";
            WriteDiagnostic("warning", "Online speech failed: " + e.GetType().Name + ": " + e.Message);
            throw new PluginScopeException(PluginErrorCode.InternalError, detail);
        }
        finally { lock (_speechCancellationGate) _speechOperation = null; _speechGate.Release(); }
    }

    private void DisposeSpeech()
    {
        _speechLifetime.Cancel();
        _previewAudio = null;
        _ = StopPreviewSafeAsync();
    }
    private async Task StopPreviewSafeAsync()
    {
        try { await _environment.PlaySpeechPreviewAsync(_scope.Manifest.Id, null, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) { WriteDiagnostic("warning", "Preview cleanup failed: " + e.Message); }
    }
}
