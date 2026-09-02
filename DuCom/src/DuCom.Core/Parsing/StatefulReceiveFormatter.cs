using System.Globalization;
using System.Text;

namespace DuCom.Core.Parsing;

public sealed class StatefulReceiveFormatter
{
    private readonly Encoding _encoding;
    private readonly Decoder _decoder;
    private readonly ReceiveDisplayMode _mode;
    private readonly bool _timestampEnabled;
    private readonly string _timestampFormat;
    private readonly int _maximumLineCharacters;
    private readonly bool _escapeNullBytes;
    private readonly ReceiveNewlinePolicy _newlinePolicy;
    private readonly StringBuilder _line = new();
    private DateTimeOffset? _lineReceivedAtUtc;
    private DateTimeOffset? _pendingInputReceivedAtUtc;
    private DateTimeOffset? _lastInputReceivedAtUtc;
    private bool _pendingCr;
    private bool _hexHasValues;

    public StatefulReceiveFormatter(
        Encoding encoding,
        ReceiveDisplayMode mode,
        bool timestampEnabled,
        int maximumLineCharacters = 4_096,
        string malformedInputReplacement = "\uFFFD",
        bool escapeNullBytes = true,
        ReceiveNewlinePolicy newlinePolicy = ReceiveNewlinePolicy.NormalizeCrLfCrLf,
        string timestampFormat = "HH:mm:ss.fff")
    {
        ArgumentNullException.ThrowIfNull(encoding);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Encoding replacementEncoding = (Encoding)encoding.Clone();
        ArgumentNullException.ThrowIfNull(malformedInputReplacement);
        replacementEncoding.DecoderFallback = new DecoderReplacementFallback(malformedInputReplacement);
        _encoding = replacementEncoding;
        _decoder = replacementEncoding.GetDecoder();
        _mode = mode;
        _timestampEnabled = timestampEnabled;
        ArgumentException.ThrowIfNullOrWhiteSpace(timestampFormat);
        _timestampFormat = timestampFormat;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCharacters);
        _maximumLineCharacters = maximumLineCharacters;
        _escapeNullBytes = escapeNullBytes;
        if (!Enum.IsDefined(newlinePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(newlinePolicy));
        }

        _newlinePolicy = newlinePolicy;
    }

    public IReadOnlyList<FormattedLine> Append(
        ReadOnlySpan<byte> bytes,
        DateTimeOffset receivedAtUtc)
    {
        if (bytes.IsEmpty)
        {
            return [];
        }

        _lastInputReceivedAtUtc = receivedAtUtc;

        return _mode == ReceiveDisplayMode.Hex
            ? AppendHex(bytes, receivedAtUtc)
            : AppendStr(bytes, receivedAtUtc);
    }

    public IReadOnlyList<FormattedLine> Flush()
    {
        if (_mode == ReceiveDisplayMode.Hex)
        {
            return FlushCurrentLine();
        }

        List<FormattedLine> lines = [];
        char[] characters = new char[_encoding.GetMaxCharCount(0)];
        int characterCount = _decoder.GetChars([], characters, flush: true);
        if (characterCount > 0)
        {
            ProcessCharacters(
                characters.AsSpan(0, characterCount),
                _pendingInputReceivedAtUtc ?? _lineReceivedAtUtc ?? _lastInputReceivedAtUtc ?? DateTimeOffset.UtcNow,
                lines);
        }

        _decoder.Reset();
        _pendingInputReceivedAtUtc = null;

        if (_pendingCr)
        {
            _pendingCr = false;
            lines.Add(CompleteLine());
        }
        else if (_lineReceivedAtUtc.HasValue && _line.Length > 0)
        {
            lines.Add(CurrentLine(isTerminated: false));
            ResetLine();
        }
        else if (_lineReceivedAtUtc.HasValue)
        {
            ResetLine();
        }

        return lines;
    }

    private List<FormattedLine> AppendHex(
        ReadOnlySpan<byte> bytes,
        DateTimeOffset receivedAtUtc)
    {
        EnsureLineStarted(receivedAtUtc);
        foreach (byte value in bytes)
        {
            if (_hexHasValues)
            {
                _line.Append(' ');
            }

            _line.Append(value.ToString("X2", CultureInfo.InvariantCulture));
            _hexHasValues = true;
        }

        List<FormattedLine> lines = EmitSoftSegments();
        if (_line.Length > 0)
        {
            lines.Add(SoftWrapLine());
        }

        return lines;
    }

    private List<FormattedLine> AppendStr(
        ReadOnlySpan<byte> bytes,
        DateTimeOffset receivedAtUtc)
    {
        _pendingInputReceivedAtUtc ??= receivedAtUtc;
        char[] characters = new char[_encoding.GetMaxCharCount(bytes.Length)];
        int characterCount = _decoder.GetChars(bytes, characters, flush: false);
        if (characterCount == 0)
        {
            return [];
        }

        List<FormattedLine> lines = [];
        ProcessCharacters(characters.AsSpan(0, characterCount), _pendingInputReceivedAtUtc.Value, lines);
        _pendingInputReceivedAtUtc = null;

        if (_line.Length > 0)
        {
            lines.Add(SoftWrapLine());
        }

        return lines;
    }

    private void ProcessCharacters(
        ReadOnlySpan<char> characters,
        DateTimeOffset receivedAtUtc,
        List<FormattedLine> lines)
    {
        if (_newlinePolicy != ReceiveNewlinePolicy.NormalizeCrLfCrLf)
        {
            throw new InvalidOperationException("Unsupported receive newline policy.");
        }

        foreach (char character in characters)
        {
            if (_pendingCr)
            {
                _pendingCr = false;
                lines.Add(CompleteLine());
                if (character == '\n')
                {
                    continue;
                }
            }

            if (character == '\r')
            {
                EnsureLineStarted(receivedAtUtc);
                _pendingCr = true;
            }
            else if (character == '\n')
            {
                EnsureLineStarted(receivedAtUtc);
                lines.Add(CompleteLine());
            }
            else
            {
                EnsureLineStarted(receivedAtUtc);
                _line.Append(character == '\0' && _escapeNullBytes ? "\\0" : character);
                if (_line.Length >= _maximumLineCharacters && !char.IsHighSurrogate(character))
                {
                    lines.Add(SoftWrapLine());
                }
            }
        }
    }

    private IReadOnlyList<FormattedLine> FlushCurrentLine()
    {
        if (!_lineReceivedAtUtc.HasValue || _line.Length == 0)
        {
            ResetLine();
            return [];
        }

        FormattedLine line = CurrentLine(isTerminated: false);
        ResetLine();
        return [line];
    }

    private void EnsureLineStarted(DateTimeOffset receivedAtUtc)
    {
        if (_lineReceivedAtUtc.HasValue)
        {
            return;
        }

        _lineReceivedAtUtc = receivedAtUtc;
        if (_timestampEnabled)
        {
            _line.Append('[')
                .Append(receivedAtUtc.ToLocalTime().ToString(_timestampFormat, CultureInfo.InvariantCulture))
                .Append("] ");
        }
    }

    private FormattedLine CompleteLine()
    {
        FormattedLine line = CurrentLine(isTerminated: true);
        ResetLine();
        return line;
    }

    private FormattedLine SoftWrapLine()
    {
        FormattedLine line = new(_line.ToString(), false, _lineReceivedAtUtc!.Value, IsSoftWrapped: true);
        _line.Clear();
        return line;
    }

    private List<FormattedLine> EmitSoftSegments()
    {
        List<FormattedLine> lines = [];
        while (_line.Length >= _maximumLineCharacters)
        {
            string segment = _line.ToString(0, _maximumLineCharacters);
            _line.Remove(0, _maximumLineCharacters);
            lines.Add(new FormattedLine(segment, false, _lineReceivedAtUtc!.Value, IsSoftWrapped: true));
        }

        return lines;
    }

    private FormattedLine CurrentLine(bool isTerminated) =>
        new(_line.ToString(), isTerminated, _lineReceivedAtUtc!.Value);

    private void ResetLine()
    {
        _line.Clear();
        _lineReceivedAtUtc = null;
        _hexHasValues = false;
    }

    private int TimestampLength => _timestampEnabled ? 15 : 0;
}
