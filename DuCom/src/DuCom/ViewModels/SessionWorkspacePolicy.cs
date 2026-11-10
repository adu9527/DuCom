namespace DuCom.ViewModels;

internal static class SessionWorkspacePolicy
{
    internal static T? ResolveActive<T>(T? active, T? selected, T? selectedRight, IReadOnlyCollection<T> sessions)
        where T : class => sessions.Contains(active!) ? active
            : sessions.Contains(selected!) ? selected
            : sessions.Contains(selectedRight!) ? selectedRight
            : null;

    public static string[] ResolveOpenPorts(
        IReadOnlyList<string> openPorts,
        IReadOnlyList<string> rightPorts,
        IReadOnlyList<string> sessionOrder)
    {
        // Tab order and split layout are not connection intent. In particular an
        // explicitly empty open list means the user closed every connection.
        return [.. openPorts.Where(port => !string.IsNullOrWhiteSpace(port)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public static int BoundMoveIndex(int requestedIndex, int count) =>
        count == 0 ? 0 : Math.Clamp(requestedIndex, 0, count - 1);
}
