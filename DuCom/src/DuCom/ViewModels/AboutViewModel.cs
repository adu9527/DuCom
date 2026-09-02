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
    public partial string Version { get; private set; } = "V0001";

    [ObservableProperty]
    public partial string ReleaseDate { get; private set; } = "2026年8月27日 09:31:26";

    [ObservableProperty]
    public partial string Author { get; private set; } = "du";

    [ObservableProperty]
    public partial string CurrentTime { get; private set; } = string.Empty;

    public void Dispose()
    {
        _clock.Stop();
        _clock.Tick -= OnTick;
        GC.SuppressFinalize(this);
    }

    private void OnTick(object? sender, EventArgs e) => UpdateNow();

    private void UpdateNow() => CurrentTime = DateTime.Now.ToString("yyyy年MM月dd日 HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
