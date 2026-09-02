namespace DuCom.Services.Shortcuts;

public sealed record ShortcutConflictResult(bool IsValid, string Message, IReadOnlyList<string> ConflictingActionIds)
{
    public static ShortcutConflictResult Valid() => new(true, string.Empty, []);

    public static ShortcutConflictResult Invalid(string message, IEnumerable<string>? conflictingActionIds = null) =>
        new(false, message, conflictingActionIds?.ToArray() ?? []);
}
