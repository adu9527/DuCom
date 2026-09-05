using System.Globalization;
using System.Reflection;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DuCom.ViewModels;

public partial class AboutViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public AboutViewModel()
    {
        _clock.Tick += OnTick;
        _clock.Start();
        UpdateNow();
    }

    [ObservableProperty]
    public partial string ProductName { get; private set; } = "DuCom";

    [ObservableProperty]
    public partial string Version { get; private set; } = "V0003";

    [ObservableProperty]
    public partial string ReleaseDate { get; private set; } = GetBuildDate();

    [ObservableProperty]
    public partial string Author { get; private set; } = "du";

    public string GitHubUrl { get; } = "https://github.com/adu9527/DuCom";

    public string QQGroupNumber { get; } = "1107820408";

    [ObservableProperty]
    public partial string CurrentTime { get; private set; } = string.Empty;

    public void Dispose()
    {
        _clock.Stop();
        _clock.Tick -= OnTick;
        GC.SuppressFinalize(this);
    }

    private void OnTick(object? sender, EventArgs e) => UpdateNow();

    private static string GetBuildDate()
    {
        string? value = typeof(AboutViewModel).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "BuildDate", StringComparison.Ordinal))
            ?.Value;
        return DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime buildDate)
                ? buildDate.ToString("yyyy年M月d日", CultureInfo.InvariantCulture)
                : "未知";
    }

    private void UpdateNow() => CurrentTime = DateTime.Now.ToString("yyyy年MM月dd日 HH:mm:ss", CultureInfo.InvariantCulture);
}
