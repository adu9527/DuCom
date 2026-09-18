using System.Text;

namespace DuCom.Core.Parsing;

/// <summary>Removes terminal control sequences while preserving ordinary text and line breaks.</summary>
public sealed class AnsiTextSanitizer
{
    private const char Escape = '\u001B';
    private const char Bell = '\u0007';
    private const int MaximumCsiCharacters = 64;
    private const int MaximumOscCharacters = 1_024;
    private ParseState _state;
    private int _sequenceLength;

    public string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        StringBuilder? output = _state == ParseState.Normal ? null : new StringBuilder(text.Length);
        int plainStart = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (_state == ParseState.Normal)
            {
                if (character != Escape)
                {
                    continue;
                }

                output ??= new StringBuilder(text.Length);
                output.Append(text, plainStart, index - plainStart);
                _state = ParseState.Escaped;
                _sequenceLength = 0;
                plainStart = index + 1;
                continue;
            }

            plainStart = index + 1;
            switch (_state)
            {
                case ParseState.Escaped:
                    if (character == '[')
                    {
                        _state = ParseState.Csi;
                    }
                    else if (character == ']')
                    {
                        _state = ParseState.Osc;
                    }
                    else
                    {
                        _state = ParseState.Normal;
                    }
                    break;

                case ParseState.Csi:
                    _sequenceLength++;
                    if (character is >= '\u0040' and <= '\u007E' || _sequenceLength >= MaximumCsiCharacters)
                    {
                        _state = ParseState.Normal;
                    }
                    break;

                case ParseState.Osc:
                    _sequenceLength++;
                    if (character == Bell)
                    {
                        _state = ParseState.Normal;
                    }
                    else if (character == Escape)
                    {
                        _state = ParseState.OscEscaped;
                    }
                    else if (_sequenceLength >= MaximumOscCharacters)
                    {
                        _state = ParseState.Normal;
                    }
                    break;

                case ParseState.OscEscaped:
                    _sequenceLength++;
                    if (character == '\\')
                    {
                        _state = ParseState.Normal;
                    }
                    else if (character != Escape)
                    {
                        _state = ParseState.Osc;
                    }
                    if (_sequenceLength >= MaximumOscCharacters)
                    {
                        _state = ParseState.Normal;
                    }
                    break;
            }
        }

        if (output is null)
        {
            return text;
        }

        if (_state == ParseState.Normal && plainStart < text.Length)
        {
            output.Append(text, plainStart, text.Length - plainStart);
        }
        return output.ToString();
    }

    private enum ParseState
    {
        Normal,
        Escaped,
        Csi,
        Osc,
        OscEscaped,
    }
}
