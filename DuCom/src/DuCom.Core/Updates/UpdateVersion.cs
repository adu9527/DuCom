namespace DuCom.Core.Updates;

/// <summary>Parses release tag names and compares them against the running assembly version.</summary>
public static class UpdateVersion
{
    public static Version? TryParse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string value = tag.Trim().TrimStart('v', 'V');
        int prereleaseMarker = value.IndexOf('-');
        if (prereleaseMarker >= 0)
        {
            value = value[..prereleaseMarker];
        }

        if (value.Length == 0)
        {
            return null;
        }

        // Legacy tags such as V0003 map to 0.0.0.3.
        if (value.Length == 4 && value.All(char.IsDigit))
        {
            return int.TryParse(value, out int legacy) && legacy >= 0
                ? new Version(0, 0, 0, legacy)
                : null;
        }

        string[] segments = value.Split('.');
        if (segments.Length is < 1 or > 4)
        {
            return null;
        }

        int[] numbers = new int[4];
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (segment.Length == 0 || segment.Length > 10 || !segment.All(char.IsDigit))
            {
                return null;
            }

            if (!int.TryParse(segment, out int parsed))
            {
                return null;
            }

            numbers[index] = parsed;
        }

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    public static bool IsNewer(Version candidate, Version current) => candidate > current;

    public static string ToDisplayString(Version version) => $"V{version}";
}
