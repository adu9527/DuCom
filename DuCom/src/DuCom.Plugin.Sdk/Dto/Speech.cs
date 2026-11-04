namespace DuCom.Plugin.Dto;

public sealed record SpeechVoice(string ShortName, string FriendlyName, string Locale, string Gender);
public sealed record SpeechCue(long Offset, long Duration, string Text);
public sealed record SpeechRequest
{
    public string Text { get; init; } = "";
    public string Voice { get; init; } = "zh-CN-XiaoxiaoNeural";
    public int Rate { get; init; }
    public int Volume { get; init; }
    public int Pitch { get; init; }
    public bool WordBoundary { get; init; }
    public string Proxy { get; init; } = "";
}
public sealed record SpeechResult(string AudioBase64, IReadOnlyList<SpeechCue> Cues);

public interface IPluginSpeech
{
    Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string proxy, CancellationToken cancellationToken = default);
    Task<SpeechResult> SynthesizeChunkAsync(SpeechRequest request, CancellationToken cancellationToken = default);
    Task PreviewAsync(SpeechRequest request, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
