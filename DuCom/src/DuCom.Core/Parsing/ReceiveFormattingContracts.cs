using System.Text;

namespace DuCom.Core.Parsing;

public enum ReceiveDisplayMode
{
    Str,
    Hex,
}

public enum NewlineKind
{
    CrLf,
    Cr,
    Lf,
}

public enum ReceiveNewlinePolicy
{
    NormalizeCrLfCrLf,
}

public sealed record ReceiveFormattingProfile(
    long Version,
    string EncodingName,
    ReceiveDisplayMode DisplayMode,
    bool TimestampEnabled,
    int MaximumLineCharacters = 4_096,
    string MalformedInputReplacement = "\uFFFD",
    bool EscapeNullBytes = true,
    ReceiveNewlinePolicy NewlinePolicy = ReceiveNewlinePolicy.NormalizeCrLfCrLf,
    string TimestampFormat = "HH:mm:ss.fff",
    int UnterminatedLineIdleMilliseconds = 0)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(EncodingName);
        _ = Encoding.GetEncoding(EncodingName);
        if (!Enum.IsDefined(DisplayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(DisplayMode));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLineCharacters);
        ArgumentException.ThrowIfNullOrWhiteSpace(TimestampFormat);
        ArgumentOutOfRangeException.ThrowIfNegative(UnterminatedLineIdleMilliseconds);
        ArgumentNullException.ThrowIfNull(MalformedInputReplacement);
        if (!Enum.IsDefined(NewlinePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(NewlinePolicy));
        }
    }

    public StatefulReceiveFormatter CreateFormatter()
    {
        Validate();
        return new StatefulReceiveFormatter(
            Encoding.GetEncoding(EncodingName),
            DisplayMode,
            TimestampEnabled,
            MaximumLineCharacters,
            MalformedInputReplacement,
            EscapeNullBytes,
            NewlinePolicy,
            TimestampFormat,
            UnterminatedLineIdleMilliseconds == 0
                ? null
                : TimeSpan.FromMilliseconds(UnterminatedLineIdleMilliseconds));
    }
}

public readonly record struct FormattedLine(
    string Text,
    bool IsTerminated,
    DateTimeOffset ReceivedAtUtc,
    bool IsSoftWrapped = false);
