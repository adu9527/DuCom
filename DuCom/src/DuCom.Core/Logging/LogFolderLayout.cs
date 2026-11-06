using System.Globalization;

namespace DuCom.Core.Logging;

/// <summary>
/// Canonical layout under the log root:
/// <c>{root}/{yyyy-MM-dd}/serial</c>, <c>{root}/{yyyy-MM-dd}/system</c> and
/// <c>{root}/{yyyy-MM-dd}/plugins/{pluginId}</c>. Everything that writes logs goes through
/// these helpers so no component invents its own date or category folder.
/// </summary>
public static class LogFolderLayout
{
    public const string SerialFolderName = "serial";
    public const string SystemFolderName = "system";
    public const string PluginsFolderName = "plugins";

    public static string DateFolderName(DateTimeOffset now) =>
        now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string DateRoot(string logRoot, DateTimeOffset now) =>
        Path.Combine(logRoot, DateFolderName(now));

    public static string Serial(string logRoot, DateTimeOffset now) =>
        Path.Combine(DateRoot(logRoot, now), SerialFolderName);

    public static string System(string logRoot, DateTimeOffset now) =>
        Path.Combine(DateRoot(logRoot, now), SystemFolderName);

    public static string Plugin(string logRoot, string pluginId, DateTimeOffset now) =>
        Path.Combine(DateRoot(logRoot, now), PluginsFolderName, SafePluginId(pluginId));

    /// <summary>
    /// Plugin ids are DPP identifiers, but a hostile or malformed one must never be able to
    /// escape the log root or collide with another plugin's folder.
    /// </summary>
    public static string SafePluginId(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            return "unknown-plugin";
        }

        char[] invalid = [.. Path.GetInvalidFileNameChars(), Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, ':'];
        string sanitized = string.Join("_", pluginId.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        return sanitized.Length == 0 ? "unknown-plugin" : sanitized;
    }
}
