using DuCom.Plugin.Dto;
using DuCom.PluginHost.Core;
using DuCom.Plugins.Audio;
using Xunit;

namespace DuCom.Plugins.Tests;

public sealed class AudioTests
{
    [Theory]
    [InlineData(0, "52BC7A17B50DFEC9-28C4394775C8751CD2F6859C030C06DB1489ACAB3B3A7651831FC26895177ED6")]
    [InlineData(1793664000, "2160CE7813471E90-ED971CC8747744182A43931B86E7441E13B4FED48C90F23888CF2BD31FC765B7")]
    public void V2TokenMatchesUpstreamVectors(long timestamp, string expected) => Assert.Equal(expected, EdgeSpeechToken.Generate(DateTimeOffset.FromUnixTimeSeconds(timestamp)));

    [Fact]
    public void ChunkingPreservesSurrogatePairsAndAllContent()
    {
        string text = new string('中', 349) + "😀" + new string('文', 500);
        IReadOnlyList<string> chunks = AudioText.Split(text);
        Assert.All(chunks, chunk => { Assert.InRange(chunk.Length, 1, 350); Assert.False(char.IsHighSurrogate(chunk[^1])); Assert.False(char.IsLowSurrogate(chunk[0])); });
        Assert.Equal(text, string.Concat(chunks));
    }

    [Fact]
    public void SrtUsesGlobalTicksAndSkipsInvalidCues()
    {
        string srt = AudioText.ToSrt([new(12_345_000, 10_000_000, "你好"), new(0, 0, "invalid")]);
        Assert.Equal("1\n00:00:01,234 --> 00:00:02,234\n你好\n\n", srt);
    }

    [Fact]
    public void SpeechCannotChooseEndpointOrInjectSsml()
    {
        Assert.Throws<ArgumentException>(() => EdgeSpeechClient.Validate(new() { Text = "hello", Voice = "voice'><break/>" }));
        Assert.Throws<ArgumentException>(() => EdgeSpeechClient.Validate(new() { Text = new string('a', 351) }));
        Assert.Throws<ArgumentException>(() => EdgeSpeechClient.ResolveProxy("file:///C:/secret"));
        Assert.Throws<ArgumentException>(() => EdgeSpeechClient.ResolveProxy("http://user:secret@localhost:8080"));
        Assert.Null(EdgeSpeechClient.ResolveProxy("direct"));
    }

    [Fact]
    public async Task LiveMicrosoftSpeechSmokeTest()
    {
        if (Environment.GetEnvironmentVariable("DUCOM_SPEECH_LIVE_TEST") != "1") return;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(100));
        EdgeSpeechClient client = new();
        IReadOnlyList<SpeechVoice> voices = await client.GetVoicesAsync("", deadline.Token);
        Assert.Contains(voices, v => v.ShortName == "zh-CN-XiaoxiaoNeural");
        SpeechResult result = await client.SynthesizeAsync(new() { Text = "你好，这是 DuCom 在线语音测试。" }, deadline.Token);
        Assert.True(Convert.FromBase64String(result.AudioBase64).Length > 1000);
        Assert.NotEmpty(result.Cues);
    }
}
