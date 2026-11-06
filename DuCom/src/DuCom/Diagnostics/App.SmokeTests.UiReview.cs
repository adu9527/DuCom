using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DuCom;

public partial class App
{
    /// <summary>Opt-in, non-destructive layout review of real settings, including fractional DPI renders.</summary>
    private async void RunUiReviewSmokeTest(Window owner, string outputDirectory)
    {
        string originalTheme = CurrentThemeMode;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            foreach (string theme in new[] { "Dark", "Light" })
            {
                ApplyTheme(theme);
                SettingsWindow settings = new() { Owner = owner, DataContext = owner.DataContext };
                settings.Show();
                foreach (int category in new[] { 0, 1, 3, 4, 5, 6 })
                {
                    settings.SelectCategory(category);
                    await Task.Delay(250);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    settings.UpdateLayout();
                    CaptureReviewPng(settings, Path.Combine(outputDirectory, $"{theme}-settings-{category}-100.png"), 96);
                    // Rendering at multiple device scales verifies vector edges and fractional type
                    // without changing the user's OS display configuration or resizing other windows.
                    if (category is 3 or 4 or 6)
                    {
                        foreach (double dpi in new[] { 120d, 144d, 192d })
                            CaptureReviewPng(settings, Path.Combine(outputDirectory, $"{theme}-settings-{category}-{dpi / 96 * 100:0}.png"), dpi);
                        settings.Width = settings.MinWidth;
                        settings.Height = settings.MinHeight;
                        settings.UpdateLayout();
                        CaptureReviewPng(settings, Path.Combine(outputDirectory, $"{theme}-settings-{category}-minimum.png"), 96);
                        settings.Width = 920;
                        settings.Height = 660;
                    }
                    if (!settings.UseLayoutRounding || TextOptions.GetTextFormattingMode(settings) != TextFormattingMode.Display)
                        throw new InvalidOperationException("FluentWindow did not inherit crisp device-independent rendering defaults.");
                }
                settings.Close();
            }
            ApplyTheme(originalTheme);
            DiagnosticLog?.Information($"UI review smoke test passed. Captures={outputDirectory}");
            await PrepareShutdownAsync();
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("UI review smoke test failed.", exception);
            Shutdown(-15);
        }
    }

    private static void CaptureReviewPng(Window window, string path, double dpi)
    {
        // FluentWindow's non-client composition is not capturable by rendering the HWND
        // visual. Capture its actual content tree at the requested DPI instead.
        FrameworkElement content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        double scale = dpi / 96;
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth * scale),
            (int)Math.Ceiling(content.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        byte[] sample = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), sample, 4, 0);
        if (sample[3] == 0) throw new InvalidOperationException("Screenshot captured no rendered content.");
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
