using System.Globalization;
using System.Text;

namespace DuCom.Core.Parsing;

public sealed class AnsiParser
{
    private const char Escape = '\u001B';
    private const char CsiIntroducer = '[';
    private const char OscIntroducer = ']';
    private const char StringTerminator = '\\';
    private const char Bell = '\u0007';

    private readonly StringBuilder _pendingText = new();
    private readonly StringBuilder _csiBuffer = new();
    private readonly StringBuilder _oscBuffer = new();
    private AnsiStyle _currentStyle = AnsiStyle.Default;
    private ParseState _state = ParseState.Normal;

    public AnsiStyle CurrentStyle => _currentStyle;

    /// <summary>
    /// True when no escape sequence is in progress (not mid-CSI/OSC) and the active style is
    /// the default. A segment processed while this is true renders identically with or without
    /// consulting persistent state, allowing safe plain-text fast paths.
    /// </summary>
    public bool IsAtNeutralPlainState =>
        _state == ParseState.Normal &&
        _currentStyle == AnsiStyle.Default &&
        _csiBuffer.Length == 0 &&
        _oscBuffer.Length == 0;

    public IReadOnlyList<AnsiRun> Parse(ReadOnlySpan<char> input)
    {
        List<AnsiRun> runs = [];
        foreach (char character in input)
        {
            ProcessCharacter(character, runs);
        }

        if (_state == ParseState.Normal)
        {
            EmitPendingText(runs);
        }

        return runs;
    }

    public IReadOnlyList<AnsiRun> Flush()
    {
        List<AnsiRun> runs = [];
        if (_state != ParseState.Normal)
        {
            _state = ParseState.Normal;
            _csiBuffer.Clear();
            _oscBuffer.Clear();
        }

        EmitPendingText(runs);
        return runs;
    }

    public void Reset()
    {
        _pendingText.Clear();
        _csiBuffer.Clear();
        _oscBuffer.Clear();
        _currentStyle = AnsiStyle.Default;
        _state = ParseState.Normal;
    }

    private void ProcessCharacter(char character, List<AnsiRun> runs)
    {
        switch (_state)
        {
            case ParseState.Normal:
                if (character == Escape)
                {
                    EmitPendingText(runs);
                    _state = ParseState.Escaped;
                }
                else
                {
                    _pendingText.Append(character);
                }

                break;

            case ParseState.Escaped:
                if (character == CsiIntroducer)
                {
                    _state = ParseState.Csi;
                    _csiBuffer.Clear();
                }
                else if (character == OscIntroducer)
                {
                    _state = ParseState.Osc;
                    _oscBuffer.Clear();
                }
                else
                {
                    _state = ParseState.Normal;
                }

                break;

            case ParseState.Csi:
                if (IsCsiFinalByte(character))
                {
                    _csiBuffer.Append(character);
                    ApplyCsiSequence(_csiBuffer.ToString());
                    _csiBuffer.Clear();
                    _state = ParseState.Normal;
                }
                else if (IsCsiIntermediateOrParameter(character))
                {
                    _csiBuffer.Append(character);
                }
                else if (character == Escape)
                {
                    _csiBuffer.Clear();
                    _state = ParseState.Escaped;
                }
                else
                {
                    _csiBuffer.Clear();
                    _state = ParseState.Normal;
                }

                break;

            case ParseState.Osc:
                if (character == Bell || (character == Escape && _oscBuffer.Length > 0 && _oscBuffer[^1] == Escape))
                {
                    _oscBuffer.Clear();
                    _state = ParseState.Normal;
                }
                else if (character == Escape)
                {
                    _oscBuffer.Append(character);
                }
                else if (character == StringTerminator && _oscBuffer.Length > 0 && _oscBuffer[^1] == Escape)
                {
                    _oscBuffer.Clear();
                    _state = ParseState.Normal;
                }
                else
                {
                    _oscBuffer.Append(character);
                }

                break;
        }
    }

    private static bool IsCsiFinalByte(char character) => character is >= '\u0040' and <= '\u007E';

    private static bool IsCsiIntermediateOrParameter(char character) =>
        character is >= '\u0020' and <= '\u003F';

    private void ApplyCsiSequence(string sequence)
    {
        if (string.IsNullOrEmpty(sequence))
        {
            return;
        }

        char finalByte = sequence[^1];
        if (finalByte != 'm')
        {
            return;
        }

        string parameters = sequence[..^1];
        if (string.IsNullOrEmpty(parameters))
        {
            _currentStyle = AnsiStyle.Default;
            return;
        }

        ApplySgrParameters(parameters);
    }

    private void ApplySgrParameters(string parameters)
    {
        ReadOnlySpan<char> span = parameters.AsSpan();
        int index = 0;
        while (index < span.Length)
        {
            int semicolon = span[index..].IndexOf(';');
            ReadOnlySpan<char> token = semicolon < 0
                ? span[index..]
                : span.Slice(index, semicolon);

            if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            {
                if (code is 38 or 48)
                {
                    (int consumed, AnsiStyle style) = ApplyExtendedColor(
                        code == 38,
                        semicolon < 0 ? ReadOnlySpan<char>.Empty : span[(index + token.Length + 1)..]);
                    _currentStyle = style;
                    index += token.Length + 1 + consumed;
                }
                else
                {
                    _currentStyle = ApplySgrCode(code);
                    index += token.Length;
                }
            }
            else
            {
                index += token.Length;
            }

            if (semicolon >= 0)
            {
                index++;
            }
        }
    }

    private AnsiStyle ApplySgrCode(int code)
    {
        AnsiStyle style = _currentStyle;
        switch (code)
        {
            case 0:
                return AnsiStyle.Default;
            case 1:
                return style with { Bold = true };
            case 2:
            case 22:
                return style with { Bold = false };
            case 4:
                return style with { Underline = true };
            case 24:
                return style with { Underline = false };
            case 7:
                return style with { Reverse = true };
            case 27:
                return style with { Reverse = false };
            case 30:
            case 31:
            case 32:
            case 33:
            case 34:
            case 35:
            case 36:
            case 37:
                {
                    (byte r, byte g, byte b) = GetStandardColorRgb(code - 30);
                    return style with { ForegroundR = r, ForegroundG = g, ForegroundB = b };
                }

            case 39:
                return style with { ForegroundR = null, ForegroundG = null, ForegroundB = null };
            case 40:
            case 41:
            case 42:
            case 43:
            case 44:
            case 45:
            case 46:
            case 47:
                {
                    (byte r, byte g, byte b) = GetStandardColorRgb(code - 40);
                    return style with { BackgroundR = r, BackgroundG = g, BackgroundB = b };
                }

            case 49:
                return style with { BackgroundR = null, BackgroundG = null, BackgroundB = null };
            case 90:
            case 91:
            case 92:
            case 93:
            case 94:
            case 95:
            case 96:
            case 97:
                {
                    (byte r, byte g, byte b) = GetBrightColorRgb(code - 90);
                    return style with { ForegroundR = r, ForegroundG = g, ForegroundB = b };
                }

            case 100:
            case 101:
            case 102:
            case 103:
            case 104:
            case 105:
            case 106:
            case 107:
                {
                    (byte r, byte g, byte b) = GetBrightColorRgb(code - 100);
                    return style with { BackgroundR = r, BackgroundG = g, BackgroundB = b };
                }
        }

        return style;
    }

    private (int Consumed, AnsiStyle Style) ApplyExtendedColor(bool isForeground, ReadOnlySpan<char> remainingParameters)
    {
        AnsiStyle style = _currentStyle;
        if (remainingParameters.IsEmpty)
        {
            return (0, style);
        }

        int index = 0;
        int semicolon = remainingParameters.IndexOf(';');
        ReadOnlySpan<char> modeToken = semicolon < 0
            ? remainingParameters
            : remainingParameters[..semicolon];
        if (!int.TryParse(modeToken, NumberStyles.None, CultureInfo.InvariantCulture, out int mode))
        {
            return (modeToken.Length, style);
        }

        index += modeToken.Length;
        if (mode != 5 && mode != 2)
        {
            return (index, style);
        }

        if (semicolon < 0)
        {
            return (index, style);
        }

        ReadOnlySpan<char> current = remainingParameters[(semicolon + 1)..];
        index++;

        if (mode == 5)
        {
            int nextSemicolon = current.IndexOf(';');
            ReadOnlySpan<char> colorToken = nextSemicolon < 0 ? current : current[..nextSemicolon];
            if (int.TryParse(colorToken, NumberStyles.None, CultureInfo.InvariantCulture, out int colorIndex))
            {
                (byte r, byte g, byte b) = Get256Color(colorIndex);
                style = isForeground
                    ? style with { ForegroundR = r, ForegroundG = g, ForegroundB = b }
                    : style with { BackgroundR = r, BackgroundG = g, BackgroundB = b };
                index += colorToken.Length;
            }
        }
        else if (mode == 2)
        {
            int[] components = new int[3];
            int parsedCount = 0;
            while (parsedCount < 3 && !current.IsEmpty)
            {
                int nextSemicolon = current.IndexOf(';');
                ReadOnlySpan<char> componentToken = nextSemicolon < 0 ? current : current[..nextSemicolon];
                if (!int.TryParse(componentToken, NumberStyles.None, CultureInfo.InvariantCulture, out int component))
                {
                    break;
                }

                components[parsedCount++] = component;
                index += componentToken.Length;
                if (nextSemicolon >= 0)
                {
                    index++;
                    current = current[(nextSemicolon + 1)..];
                }
                else
                {
                    current = ReadOnlySpan<char>.Empty;
                }
            }

            if (parsedCount == 3)
            {
                byte r = ClampToByte(components[0]);
                byte g = ClampToByte(components[1]);
                byte b = ClampToByte(components[2]);
                style = isForeground
                    ? style with { ForegroundR = r, ForegroundG = g, ForegroundB = b }
                    : style with { BackgroundR = r, BackgroundG = g, BackgroundB = b };
            }
        }

        return (index, style);
    }

    private static byte? GetStandardColorR(int index)
    {
        (byte r, byte g, byte b) = GetStandardColorRgb(index);
        return r;
    }

    private static byte? GetBrightColorR(int index)
    {
        (byte r, byte g, byte b) = GetBrightColorRgb(index);
        return r;
    }

    private static (byte R, byte G, byte B) GetStandardColorRgb(int index) => index switch
    {
        0 => (0x00, 0x00, 0x00),
        1 => (0xCC, 0x40, 0x40),
        2 => (0x4E, 0x9A, 0x06),
        3 => (0xC4, 0xA0, 0x00),
        4 => (0x34, 0x65, 0xA4),
        5 => (0x75, 0x50, 0x7B),
        6 => (0x06, 0x98, 0x9A),
        7 => (0xD3, 0xD7, 0xCF),
        _ => (0xD3, 0xD7, 0xCF),
    };

    private static (byte R, byte G, byte B) GetBrightColorRgb(int index) => index switch
    {
        0 => (0x55, 0x57, 0x53),
        1 => (0xEF, 0x29, 0x29),
        2 => (0x8A, 0xE2, 0x34),
        3 => (0xFC, 0xE9, 0x4F),
        4 => (0x73, 0x9F, 0xCF),
        5 => (0xAD, 0x7F, 0xA8),
        6 => (0x34, 0xE2, 0xE2),
        7 => (0xEE, 0xEE, 0xEC),
        _ => (0xEE, 0xEE, 0xEC),
    };

    private static (byte R, byte G, byte B) Get256Color(int index)
    {
        if (index < 0)
        {
            index = 0;
        }
        else if (index > 255)
        {
            index = 255;
        }

        if (index < 16)
        {
            return index < 8
                ? GetStandardColorRgb(index)
                : GetBrightColorRgb(index - 8);
        }

        if (index >= 232)
        {
            byte gray = (byte)(8 + (index - 232) * 10);
            return (gray, gray, gray);
        }

        index -= 16;
        int r = index / 36;
        int g = (index / 6) % 6;
        int b = index % 6;
        return (
            (byte)(r == 0 ? 0 : 55 + r * 40),
            (byte)(g == 0 ? 0 : 55 + g * 40),
            (byte)(b == 0 ? 0 : 55 + b * 40));
    }

    private static byte ClampToByte(int value) =>
        value < 0 ? byte.MinValue : value > 255 ? byte.MaxValue : (byte)value;

    private void EmitPendingText(List<AnsiRun> runs)
    {
        if (_pendingText.Length == 0)
        {
            return;
        }

        runs.Add(new AnsiRun(_pendingText.ToString(), _currentStyle));
        _pendingText.Clear();
    }

    private enum ParseState
    {
        Normal,
        Escaped,
        Csi,
        Osc,
    }
}
