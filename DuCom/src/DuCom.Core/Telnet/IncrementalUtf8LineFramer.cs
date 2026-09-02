using System.Text;

namespace DuCom.Core.Telnet;

/// <summary>
/// Incremental UTF-8 command framer for Telnet client input. Bytes are pushed in whatever
/// chunks TCP delivers; the framer keeps a persistent <see cref="Decoder"/> so a multibyte
/// character split across segments is never corrupted, and emits one string per completed
/// line. CR, LF, and CRLF all terminate a line; newline bytes are frame boundaries, not
/// payload characters. A trailing partial line (no terminator yet) stays buffered until a
/// terminator arrives or <see cref="Flush"/> is called (client disconnect). Empty lines
/// are not emitted as commands.
/// </summary>
/// <remarks>
/// Bounded memory (2026-08-28 review): a client that streams bytes without ever sending a
/// newline cannot grow the pending buffer indefinitely. The pending command is capped at
/// <paramref name="maximumCommandLength"/>; when the cap is exceeded the oversized line is
/// dropped (counted in <see cref="OverflowCount"/>), everything up to and including the
/// next newline is discarded so the oversized input can never leak out as fake commands,
/// and framing resumes normally after the terminator. The default cap is 8 KB, far above
/// any sane serial command line.
/// </remarks>
public sealed class IncrementalUtf8LineFramer
{
    public const int DefaultMaximumCommandLength = 8 * 1024;

    private readonly int _maximumCommandLength;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();
    private bool _discardingUntilTerminator;

    public IncrementalUtf8LineFramer(int maximumCommandLength = DefaultMaximumCommandLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumCommandLength, 0);
        _maximumCommandLength = maximumCommandLength;
    }

    /// <summary>Number of oversized commands dropped since construction (or the last Reset).</summary>
    public int OverflowCount { get; private set; }

    /// <summary>Feeds one TCP chunk and returns every completed non-empty line, in order.</summary>
    public IReadOnlyList<string> Append(ReadOnlySpan<byte> chunk)
    {
        List<string>? lines = null;
        byte[] bytes = chunk.ToArray();
        char[] decoded = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        int decodedLength = _decoder.GetChars(bytes, 0, bytes.Length, decoded, 0, flush: false);
        for (int index = 0; index < decodedLength; index++)
        {
            char character = decoded[index];
            if (character is '\r' or '\n')
            {
                if (_discardingUntilTerminator)
                {
                    // End of the dropped oversized line: resume normal framing here.
                    _discardingUntilTerminator = false;
                    continue;
                }

                EmitPending(ref lines);
            }
            else if (!_discardingUntilTerminator)
            {
                _pending.Append(character);
                if (_pending.Length > _maximumCommandLength)
                {
                    OverflowCount++;
                    _pending.Clear();
                    _discardingUntilTerminator = true;
                }
            }
        }

        return lines as IReadOnlyList<string> ?? [];
    }

    /// <summary>Returns the buffered partial line (used when a client disconnects mid-line). An oversized line being discarded yields null.</summary>
    public string? Flush()
    {
        if (_discardingUntilTerminator || _pending.Length == 0)
        {
            return null;
        }

        string remainder = _pending.ToString();
        _pending.Clear();
        return remainder;
    }

    public void Reset()
    {
        _pending.Clear();
        _decoder.Reset();
        _discardingUntilTerminator = false;
        OverflowCount = 0;
    }

    private void EmitPending(ref List<string>? lines)
    {
        if (_pending.Length == 0)
        {
            return;
        }

        lines ??= [];
        lines.Add(_pending.ToString());
        _pending.Clear();
    }
}
