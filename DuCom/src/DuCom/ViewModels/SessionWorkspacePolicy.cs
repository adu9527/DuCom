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
        IEnumerable<string> ports = openPorts.Count > 0
            ? openPorts
            : rightPorts.Concat(sessionOrder.Where(port =>
                !rightPorts.Contains(port, StringComparer.OrdinalIgnoreCase)).Take(1));
        return [.. ports.Where(port => !string.IsNullOrWhiteSpace(port)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public static int BoundMoveIndex(int requestedIndex, int count) =>
        count == 0 ? 0 : Math.Clamp(requestedIndex, 0, count - 1);
}
