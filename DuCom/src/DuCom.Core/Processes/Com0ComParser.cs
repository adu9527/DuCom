namespace DuCom.Core.Processes;

public static class Com0ComParser
{
    public static string ResolveSetupcPath(
        IEnumerable<string> discoveredCandidates,
        string? savedPath,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(discoveredCandidates);
        ArgumentNullException.ThrowIfNull(fileExists);

        string? discovered = discoveredCandidates.FirstOrDefault(fileExists);
        if (!string.IsNullOrEmpty(discovered))
        {
            return discovered;
        }

        return !string.IsNullOrWhiteSpace(savedPath) && fileExists(savedPath)
            ? savedPath
            : string.Empty;
    }

    public static bool IsAllowedArguments(string arguments, IReadOnlyList<string> allowedVerbs)
    {
        string verb = (arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty)
            .ToLowerInvariant();
        return allowedVerbs.Contains(verb);
    }

    public static IReadOnlyList<Com0ComPortEntry> ParseList(string output)
    {
        List<Com0ComPortEntry> entries = [];
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith("CNC", StringComparison.Ordinal) || !line.Contains(' '))
            {
                continue;
            }

            int firstSpace = line.IndexOf(' ');
            string id = line[..firstSpace];
            string? portName = null;
            Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in line[(firstSpace + 1)..].Split(','))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = pair[..equals].Trim();
                string value = pair[(equals + 1)..].Trim();
                options[key] = value;
                if (key.Equals("PortName", StringComparison.OrdinalIgnoreCase))
                {
                    portName = value;
                }
            }

            if (portName is not null)
            {
                entries.Add(new Com0ComPortEntry(id, portName, options, line));
            }
        }

        return entries;
    }

    public static IReadOnlyList<Com0ComPortPair> PairEntries(IReadOnlyList<Com0ComPortEntry> entries)
    {
        Dictionary<int, Com0ComPortEntry> sideA = [];
        Dictionary<int, Com0ComPortEntry> sideB = [];
        foreach (Com0ComPortEntry entry in entries)
        {
            if (entry.Id.Length <= 4 || !int.TryParse(entry.Id[4..], out int number))
            {
                continue;
            }

            if (entry.Id.StartsWith("CNCA", StringComparison.Ordinal))
            {
                sideA[number] = entry;
            }
            else if (entry.Id.StartsWith("CNCB", StringComparison.Ordinal))
            {
                sideB[number] = entry;
            }
        }

        return sideA.Keys.Order()
            .Where(sideB.ContainsKey)
            .Select(number => new Com0ComPortPair(number, sideA[number], sideB[number]))
            .ToArray();
    }

    public static bool IsValidPortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string upper = name.Trim().ToUpperInvariant();
        return upper.StartsWith("COM", StringComparison.Ordinal) &&
            int.TryParse(upper[3..], out int value) &&
            value > 0;
    }
}

public sealed record Com0ComPortEntry(
    string Id,
    string PortName,
    IReadOnlyDictionary<string, string> Options,
    string RawLine);

public sealed record Com0ComPortPair(int PairNumber, Com0ComPortEntry SideA, Com0ComPortEntry SideB)
{
    public string Display => $"#{PairNumber}: {SideA.PortName} <-> {SideB.PortName}";
}
