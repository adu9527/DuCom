using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuCom.Plugin.Dto;

namespace DuCom.PluginHost.Core;

/// <summary>Fixed Microsoft endpoints only; honors system/environment proxy or an explicit proxy.</summary>
public sealed class EdgeSpeechClient
{
    private const string BaseUrl = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
    private const string Agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";
    private TimeSpan _clockOffset;
    public static IWebProxy? ResolveProxy(string proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return HttpClient.DefaultProxy;
        if (proxy == "direct") return null;
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https" or "socks5") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("代理需为 http://主机:端口、https://主机:端口 或 socks5://主机:端口；留空使用系统代理，direct 表示直连。");
        return new WebProxy(uri);
    }

    public async Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(string proxy, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await GetVoicesOnceAsync(proxy, ct).ConfigureAwait(false); }
            catch (Exception e) when (attempt == 0 && !ct.IsCancellationRequested && e is HttpRequestException or IOException or OperationCanceledException)
            { await Task.Delay(700, ct).ConfigureAwait(false); }
        }
    }

    private async Task<IReadOnlyList<SpeechVoice>> GetVoicesOnceAsync(string proxy, CancellationToken ct)
    {
        using HttpClientHandler handler = new() { AutomaticDecompression = DecompressionMethods.All, Proxy = ResolveProxy(proxy), UseProxy = proxy != "direct", AllowAutoRedirect = false };
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);
        http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        http.DefaultRequestHeaders.Add("Cookie", "muid=" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ";");
        using HttpResponseMessage response = await http.GetAsync("https://" + BaseUrl + "/voices/list?trustedclienttoken=" + EdgeSpeechToken.TrustedClientToken, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.Headers.Date is { } date) _clockOffset = date - DateTimeOffset.UtcNow;
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using MemoryStream body = new();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (body.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("语音列表超过大小限制。");
            body.Write(buffer, 0, count);
        }
        using JsonDocument json = JsonDocument.Parse(body.ToArray());
        return json.RootElement.EnumerateArray().Where(v => !v.GetProperty("Locale").GetString()!.StartsWith("iu-", StringComparison.OrdinalIgnoreCase))
            .Select(v => new SpeechVoice(v.GetProperty("ShortName").GetString()!, v.GetProperty("FriendlyName").GetString()!, v.GetProperty("Locale").GetString()!, v.GetProperty("Gender").GetString()!))
            .OrderBy(v => v.Locale.StartsWith("zh-", StringComparison.Ordinal) ? 0 : 1).ThenBy(v => v.Locale, StringComparer.Ordinal).ThenBy(v => v.ShortName, StringComparer.Ordinal).ToArray();
    }

    public static void Validate(SpeechRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 350) throw new ArgumentException("每个合成块需为 1–350 个字符。");
        if (!Regex.IsMatch(request.Voice, "^[A-Za-z0-9-]{3,100}Neural$", RegexOptions.CultureInvariant)) throw new ArgumentException("语音名称无效。");
        if (request.Rate is < -100 or > 200 || request.Volume is < -100 or > 100 || request.Pitch is < -50 or > 50) throw new ArgumentException("语音参数超出范围。");
        ResolveProxy(request.Proxy);
    }

    public async Task<SpeechResult> SynthesizeAsync(SpeechRequest request, CancellationToken ct)
    {
        Validate(request);
        for (int attempt = 0; ; attempt++)
        {
            try { return await SynthesizeOnceAsync(request, ct).ConfigureAwait(false); }
            catch (Exception e) when (attempt == 0 && !ct.IsCancellationRequested && e is WebSocketException or HttpRequestException or IOException)
            {
                // Refresh server time before retrying a failed handshake; no partial block is published.
                try { await GetVoicesAsync(request.Proxy, ct).ConfigureAwait(false); }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<SpeechResult> SynthesizeOnceAsync(SpeechRequest request, CancellationToken ct)
    {
        using ClientWebSocket socket = new();
        socket.Options.Proxy = ResolveProxy(request.Proxy);
        socket.Options.SetRequestHeader("User-Agent", Agent);
        socket.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        socket.Options.SetRequestHeader("Pragma", "no-cache");
        socket.Options.SetRequestHeader("Cache-Control", "no-cache");
        socket.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        socket.Options.SetRequestHeader("Cookie", "muid=" + Guid.NewGuid().ToString("N").ToUpperInvariant() + ";");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        string uri = "wss://" + BaseUrl + "/edge/v1?TrustedClientToken=" + EdgeSpeechToken.TrustedClientToken
            + "&ConnectionId=" + Guid.NewGuid().ToString("N") + "&Sec-MS-GEC=" + EdgeSpeechToken.Generate(DateTimeOffset.UtcNow + _clockOffset)
            + "&Sec-MS-GEC-Version=2-143.0.3650.75";
        using (CancellationTokenSource connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new Uri(uri), connect.Token).ConfigureAwait(false);
        }
        string timestamp = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", CultureInfo.InvariantCulture);
        string word = request.WordBoundary ? "true" : "false", sentence = request.WordBoundary ? "false" : "true";
        await SendAsync(socket, $"X-Timestamp:{timestamp}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n"
            + "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"" + sentence + "\",\"wordBoundaryEnabled\":\"" + word + "\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n", ct).ConfigureAwait(false);
        string text = SecurityElement.Escape(Regex.Replace(request.Text, "[\\x00-\\x08\\x0B\\x0C\\x0E-\\x1F]", " "))!;
        string voice = request.Voice;
        Match match = Regex.Match(voice, "^([a-z]{2,3})-([A-Z]{2})-(.+Neural)$", RegexOptions.CultureInvariant);
        if (match.Success) voice = $"Microsoft Server Speech Text to Speech Voice ({match.Groups[1].Value}-{match.Groups[2].Value}, {match.Groups[3].Value})";
        string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'><voice name='{voice}'><prosody pitch='{Signed(request.Pitch)}Hz' rate='{Signed(request.Rate)}%' volume='{Signed(request.Volume)}%'>{text}</prosody></voice></speak>";
        await SendAsync(socket, $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{timestamp}Z\r\nPath:ssml\r\n\r\n{ssml}", ct).ConfigureAwait(false);
        using MemoryStream audio = new();
        List<SpeechCue> cues = [];
        byte[] buffer = new byte[16 * 1024];
        bool ended = false;
        while (!ended)
        {
            using MemoryStream message = new();
            WebSocketReceiveResult received;
            using CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), idle.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close) throw new IOException("语音服务提前关闭连接，请重试。");
                if (message.Length + received.Count > 512 * 1024) throw new InvalidDataException("语音服务消息超过大小限制。");
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            byte[] bytes = message.ToArray();
            if (received.MessageType == WebSocketMessageType.Binary)
            {
                if (bytes.Length < 2) throw new InvalidDataException("音频消息头不完整。");
                int header = BinaryPrimitives.ReadUInt16BigEndian(bytes);
                if (header + 2 > bytes.Length) throw new InvalidDataException("音频消息长度无效。");
                string headers = Encoding.UTF8.GetString(bytes, 2, header);
                if (!headers.Contains("Path:audio", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("音频消息类型无效。");
                int length = bytes.Length - header - 2;
                if (audio.Length + length > 256 * 1024) throw new InvalidDataException("单块音频超过大小限制，请缩短文本或提高语速。");
                audio.Write(bytes, header + 2, length);
            }
            else
            {
                string messageText = Encoding.UTF8.GetString(bytes);
                int separator = messageText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (separator < 0) throw new InvalidDataException("语音消息头不完整。");
                string headers = messageText[..separator];
                ended = headers.Contains("Path:turn.end", StringComparison.Ordinal);
                if (headers.Contains("Path:audio.metadata", StringComparison.Ordinal))
                {
                    using JsonDocument json = JsonDocument.Parse(messageText[(separator + 4)..]);
                    foreach (JsonElement meta in json.RootElement.GetProperty("Metadata").EnumerateArray())
                    {
                        if (meta.GetProperty("Type").GetString() is not ("WordBoundary" or "SentenceBoundary")) continue;
                        JsonElement data = meta.GetProperty("Data");
                        if (cues.Count >= 1000) throw new InvalidDataException("字幕数量超过限制。");
                        cues.Add(new(data.GetProperty("Offset").GetInt64(), data.GetProperty("Duration").GetInt64(), WebUtility.HtmlDecode(data.GetProperty("text").GetProperty("Text").GetString()!)));
                    }
                }
            }
        }
        if (audio.Length == 0) throw new IOException("微软未返回音频。请切换语音，或检查代理与网络后重试。");
        return new(Convert.ToBase64String(audio.ToArray()), cues);
    }
    private static string Signed(int value) => value.ToString("+0;-0;+0", CultureInfo.InvariantCulture);
    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken ct) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);
}
