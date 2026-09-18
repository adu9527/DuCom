using System.Globalization;

namespace DuCom.Core.Parsing;

public static class DisplayTextSafety
{
    private const int MinimumSuspiciousCharacters = 4;
    private const int MinimumClearlyReadableCharacters = 8;

    public static bool IsSuspiciousText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int suspiciousCharacters = 0;
        foreach (char character in text)
        {
            if (IsSuspicious(character))
            {
                suspiciousCharacters++;
            }
        }

        if (suspiciousCharacters < MinimumSuspiciousCharacters ||
            suspiciousCharacters * 8 < text.Length)
        {
            return false;
        }
        return true;
    }

    public static bool IsClearlyReadableText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ReadOnlySpan<char> content = RemoveTimestampPrefix(text.AsSpan());
        if (content.Length < MinimumClearlyReadableCharacters)
        {
            return false;
        }

        int readableCharacters = 0;
        foreach (char character in content)
        {
            if (IsSuspicious(character))
            {
                return false;
            }

            UnicodeCategory category = char.GetUnicodeCategory(character);
            if (character is >= ' ' and <= '~' ||
                category is UnicodeCategory.UppercaseLetter or
                    UnicodeCategory.LowercaseLetter or
                    UnicodeCategory.TitlecaseLetter or
                    UnicodeCategory.OtherLetter or
                    UnicodeCategory.DecimalDigitNumber or
                    UnicodeCategory.SpaceSeparator or
                    UnicodeCategory.ConnectorPunctuation or
                    UnicodeCategory.DashPunctuation or
                    UnicodeCategory.OpenPunctuation or
                    UnicodeCategory.ClosePunctuation or
                    UnicodeCategory.InitialQuotePunctuation or
                    UnicodeCategory.FinalQuotePunctuation or
                    UnicodeCategory.OtherPunctuation)
            {
                readableCharacters++;
            }
        }

        return readableCharacters * 10 >= content.Length * 9;
    }

    private static ReadOnlySpan<char> RemoveTimestampPrefix(ReadOnlySpan<char> text)
    {
        if (text.Length < 4 || text[0] != '[')
        {
            return text;
        }

        int closingBracket = text[..Math.Min(text.Length, 64)].IndexOf(']');
        if (closingBracket < 0)
        {
            return text;
        }

        int start = closingBracket + 1;
        if (start < text.Length && text[start] == ' ')
        {
            start++;
        }
        return text[start..];
    }

    private static bool IsSuspicious(char character)
    {
        if (character == '\uFFFD' || character == '\u001B')
        {
            return true;
        }

        UnicodeCategory category = char.GetUnicodeCategory(character);
        return category is UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.Surrogate or
            UnicodeCategory.PrivateUse or
            UnicodeCategory.OtherNotAssigned;
    }
}
