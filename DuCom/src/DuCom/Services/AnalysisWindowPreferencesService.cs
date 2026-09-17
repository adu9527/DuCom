using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using DuCom.Core.Persistence;

namespace DuCom.Services;

public sealed record AnalysisWindowPreference(
    double Left = double.NaN,
    double Top = double.NaN,
    double Width = 1100,
    double Height = 720,
    bool Topmost = false,
    string? PortName = null,
    string? ProfileId = null,
    int VisibleWindowSeconds = 30,
    bool FollowLatest = true);

public sealed record AnalysisWindowPreferences(
    AnalysisWindowPreference ProtocolDecoder,
    AnalysisWindowPreference VariablePlot)
{
    public static AnalysisWindowPreferences Default { get; } = new(new(), new(Width: 1200, Height: 760));
}

public sealed class AnalysisWindowPreferencesService
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    private readonly string _path;

    public AnalysisWindowPreferencesService(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DuCom", "analysis-window-preferences.json");

    public AnalysisWindowPreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return AnalysisWindowPreferences.Default;
            AnalysisWindowPreferences? value = JsonSerializer.Deserialize<AnalysisWindowPreferences>(File.ReadAllText(_path), Options);
            return value is null ? AnalysisWindowPreferences.Default : Normalize(value);
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to load analysis window preferences.", exception);
            return AnalysisWindowPreferences.Default;
        }
    }

    public void Save(AnalysisWindowPreferences value)
    {
        try { AtomicFileStore.WriteAllText(_path, JsonSerializer.Serialize(Normalize(value), Options)); }
        catch (Exception exception) { Program.DiagnosticLog?.Warning("Failed to save analysis window preferences.", exception); }
    }

    internal static AnalysisWindowPreferences Normalize(AnalysisWindowPreferences value) => new(
        Normalize(value.ProtocolDecoder), Normalize(value.VariablePlot));

    internal static AnalysisWindowPreference Normalize(AnalysisWindowPreference value)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualWidth = Math.Max(640, SystemParameters.VirtualScreenWidth);
        double virtualHeight = Math.Max(480, SystemParameters.VirtualScreenHeight);
        double width = Math.Clamp(double.IsFinite(value.Width) ? value.Width : 1100, 640, virtualWidth);
        double height = Math.Clamp(double.IsFinite(value.Height) ? value.Height : 720, 480, virtualHeight);
        double left = double.IsFinite(value.Left) ? Math.Clamp(value.Left, virtualLeft, virtualLeft + virtualWidth - 80) : double.NaN;
        double top = double.IsFinite(value.Top) ? Math.Clamp(value.Top, virtualTop, virtualTop + virtualHeight - 80) : double.NaN;
        return value with
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            PortName = string.IsNullOrWhiteSpace(value.PortName) ? null : value.PortName,
            ProfileId = string.IsNullOrWhiteSpace(value.ProfileId) ? null : value.ProfileId,
            VisibleWindowSeconds = Math.Clamp(value.VisibleWindowSeconds, 1, 86_400),
        };
    }
}
