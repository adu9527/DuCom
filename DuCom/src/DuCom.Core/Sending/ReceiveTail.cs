namespace DuCom.Core.Sending;

/// <summary>
/// Fixed-capacity receive tail used by result-check probing. Appending never throws on
/// capacity bounds: content longer than <paramref name="maxLength"/> keeps exactly its last
/// <paramref name="maxLength"/> characters. The default separator separates distinct lines;
/// continuation pieces of one logical line are joined with <see cref="ContinuationSeparator"/>.
/// </summary>
public static class ReceiveTail
{
    public const int DefaultMaxLength = 4_096;

    public const string ContinuationSeparator = "";

    public const string LineSeparator = "\n";

    public static string Append(string existing, string addition, int maxLength = DefaultMaxLength, string? separator = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(addition);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        separator ??= LineSeparator;

        string combined = addition.Length == 0 ? existing : $"{existing}{separator}{addition}";
        return combined.Length <= maxLength ? combined : combined[^maxLength..];
    }
}
