using System.Globalization;
using System.IO;
using System.Text;
using DuCom.ViewModels;

namespace DuCom.Services;

internal static class LogFilePolicy
{
    internal static string PreviewName(string format, string portName)
    {
        DateTimeOffset sample = new(2026, 8, 31, 10, 7, 42, 813, TimeSpan.Zero);
        string value = (string.IsNullOrWhiteSpace(format) ? "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}" : format)
            .Replace("{Port}", portName, StringComparison.Ordinal)
            .Replace("{yyyy}", sample.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", sample.ToString("MM", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", sample.ToString("dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HH}", sample.ToString("HH", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{mm}", sample.ToString("mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{ss}", sample.ToString("ss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{fff}", sample.ToString("fff", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{yyyyMMdd}", sample.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{HHmmss}", sample.ToString("HHmmss", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{Segment}", "0000", StringComparison.Ordinal);
        return string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)) + ".txt";
    }

    internal static Encoding GetEncoding(SessionViewModel session)
    {
        try { return Encoding.GetEncoding(session.WorkspaceSession.Settings.EncodingName); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    internal static void WriteBinary(string path, IEnumerable<string> lines, Encoding encoding)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        foreach (string line in lines)
        {
            byte[] payload = encoding.GetBytes(line);
            stream.Write(payload, 0, payload.Length);
            stream.WriteByte((byte)'\n');
        }
    }
}
