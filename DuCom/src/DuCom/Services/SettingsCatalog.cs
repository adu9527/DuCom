using System.IO;
using System.IO.Ports;
using System.Text;
using DuCom.Core.Parsing;
using DuCom.Core.Sending;

namespace DuCom.Services;

internal static class SettingsCatalog
{
    internal const int DefaultBaudRate = 1_152_000;
    internal const int DefaultDataBits = 8;
    internal const string DefaultTimestampFormat = "HH:mm:ss.fff";
    internal const string DefaultLogFontFamily = "Cascadia Mono";
    internal const double DefaultLogFontSize = 14d;
    internal const int DefaultLogRotationMegabytes = 40;
    internal const int DefaultDisplayBudgetMegabytes = 64;
    internal const string DefaultLogFileNameFormat = "{Port}-{yyyy}-{MM}-{dd} {HH}-{mm}-{ss}.{fff}";
    internal const int DefaultPrivateMemoryThresholdMiB = 1024;
    internal const int DefaultMemoryRefreshInterval = 2;
    internal const int DefaultSystemMemoryRefreshInterval = 5;
    internal const int DefaultAutoBackupPeriodDays = 7;
    internal const int DefaultTelnetPort = 23;

    internal static IReadOnlyList<int> DataBitsOptions { get; } = [5, 6, 7, 8];
    internal static IReadOnlyList<StopBits> StopBitsOptions { get; } = [StopBits.One, StopBits.Two, StopBits.OnePointFive];
    internal static IReadOnlyList<Parity> ParityOptions { get; } = Enum.GetValues<Parity>();
    internal static IReadOnlyList<Handshake> HandshakeOptions { get; } = Enum.GetValues<Handshake>();
    internal static IReadOnlyList<string> EncodingOptions { get; } = [Encoding.UTF8.WebName, Encoding.ASCII.WebName, "gb2312", "gbk"];
    internal static IReadOnlyList<ReceiveDisplayMode> ReceiveModeOptions { get; } = Enum.GetValues<ReceiveDisplayMode>();
    internal static IReadOnlyList<string> TimestampFormatOptions { get; } =
        ["HH:mm:ss", DefaultTimestampFormat, "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff"];

    internal static string DefaultLogDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");

    internal static string NormalizeTimestampFormat(string value, IReadOnlyList<string> availableFormats) =>
        availableFormats.Contains(value) ? value : DefaultTimestampFormat;

    internal static int NormalizePrivateMemoryThreshold(int value) => Math.Clamp(value, 1, 1_048_576);
    internal static int NormalizeRefreshInterval(int value) => Math.Clamp(value, 1, 60);
    internal static double NormalizeLogFontSize(double value) => value == 12 ? DefaultLogFontSize : value;
    internal static double NormalizeSearchOpacity(double value) => Math.Clamp(value, 0.2d, 1d);
    internal static int NormalizeTelnetPort(int value) => Math.Clamp(value, 1, 65_535);
    internal static double NormalizeSplitterRatio(double value) => Math.Clamp(value, 0.2d, 0.8d);

    internal static string NormalizeLogFontFamily(string value, IReadOnlyList<string> availableFamilies) =>
        availableFamilies.Contains(value, StringComparer.OrdinalIgnoreCase) ? value : DefaultLogFontFamily;
}
