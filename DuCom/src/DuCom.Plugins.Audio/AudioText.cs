using System.Globalization;
using System.Text;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.Audio;

public static class AudioText
{
    public const int MaximumTextLength = 40_000;
    public static IReadOnlyList<string> Split(string text, int maximum = 350)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 2);
        List<string> chunks = [];
        text = text.Trim();
        while (text.Length > 0)
        {
            int end = Math.Min(maximum, text.Length);
            if (end < text.Length)
            {
                if (char.IsHighSurrogate(text[end - 1])) end--;
                for (int i = end - 1; i >= end / 2; i--)
                    if ("。！？!?；;\n. ".Contains(text[i], StringComparison.Ordinal)) { end = i + 1; break; }
            }
            string chunk = text[..end].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);
            text = text[end..].TrimStart();
        }
        return chunks;
    }

    public static string ToSrt(IEnumerable<SpeechCue> cues)
    {
        StringBuilder srt = new();
        int index = 0;
        foreach (SpeechCue cue in cues.OrderBy(c => c.Offset))
        {
            if (cue.Offset < 0 || cue.Duration <= 0 || string.IsNullOrWhiteSpace(cue.Text)) continue;
            srt.Append(++index).Append('\n').Append(Timestamp(cue.Offset)).Append(" --> ").Append(Timestamp(cue.Offset + cue.Duration)).Append('\n').Append(cue.Text).Append("\n\n");
        }
        return srt.ToString();
    }
    private static string Timestamp(long ticks)
    {
        long ms = ticks / 10_000;
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
    }
    public static string SafeName(string name)
    {
        string result = new(name.Trim().Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c, StringComparison.Ordinal) ? '_' : c).ToArray());
        result = result.Trim().Trim('.');
        if (result.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) result = result[..^4];
        if (result.Length > 72) result = result[..72];
        if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result = result[..^1];
        return string.IsNullOrWhiteSpace(result) ? "DuAudio_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) : "DuAudio_" + result;
    }
}
