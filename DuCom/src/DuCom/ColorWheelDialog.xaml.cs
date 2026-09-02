using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class ColorWheelDialog : FluentWindow
{
    private const int WheelSize = 280;
    private double _hue;
    private double _saturation;
    private double _brightness = 1d;

    private ColorWheelDialog(string initialHex)
    {
        InitializeComponent();
        Color initial = ParseColor(initialHex) ?? Colors.White;
        RgbToHsv(initial, out _hue, out _saturation, out _brightness);
        BrightnessSlider.Value = _brightness;
        RenderWheel();
        UpdateSelectionMarker();
        UpdateSelectedColor();
    }

    public string SelectedHex { get; private set; } = "#FFFFFF";

    public static string? Pick(Window? owner, string initialHex)
    {
        ColorWheelDialog dialog = new(initialHex)
        {
            Owner = owner?.IsLoaded == true ? owner : Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.SelectedHex : null;
    }

    private void WheelImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WheelImage.CaptureMouse();
        UpdateFromPoint(e.GetPosition(WheelImage));
    }

    private void WheelImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            UpdateFromPoint(e.GetPosition(WheelImage));
        }
        else if (WheelImage.IsMouseCaptured)
        {
            WheelImage.ReleaseMouseCapture();
        }
    }

    private void UpdateFromPoint(Point point)
    {
        double radius = WheelSize / 2d;
        double dx = point.X - radius;
        double dy = point.Y - radius;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > radius)
        {
            double scale = radius / distance;
            dx *= scale;
            dy *= scale;
            distance = radius;
        }

        _saturation = Math.Clamp(distance / radius, 0d, 1d);
        _hue = (Math.Atan2(dy, dx) * 180d / Math.PI + 360d) % 360d;
        UpdateSelectionMarker();
        UpdateSelectedColor();
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _brightness = e.NewValue;
        if (IsLoaded)
        {
            RenderWheel();
            UpdateSelectedColor();
        }
    }

    private void RenderWheel()
    {
        int[] pixels = new int[WheelSize * WheelSize];
        double radius = WheelSize / 2d;
        for (int y = 0; y < WheelSize; y++)
        {
            for (int x = 0; x < WheelSize; x++)
            {
                double dx = x + 0.5d - radius;
                double dy = y + 0.5d - radius;
                double saturation = Math.Sqrt(dx * dx + dy * dy) / radius;
                if (saturation > 1d)
                {
                    pixels[y * WheelSize + x] = 0;
                    continue;
                }

                double hue = (Math.Atan2(dy, dx) * 180d / Math.PI + 360d) % 360d;
                Color color = HsvToRgb(hue, saturation, _brightness);
                pixels[y * WheelSize + x] = color.A << 24 | color.R << 16 | color.G << 8 | color.B;
            }
        }

        WriteableBitmap bitmap = new(WheelSize, WheelSize, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, WheelSize, WheelSize), pixels, WheelSize * 4, 0);
        bitmap.Freeze();
        WheelImage.Source = bitmap;
    }

    private void UpdateSelectionMarker()
    {
        double radius = WheelSize / 2d;
        double angle = _hue * Math.PI / 180d;
        double distance = _saturation * radius;
        Canvas.SetLeft(SelectionMarker, radius + Math.Cos(angle) * distance - SelectionMarker.Width / 2d);
        Canvas.SetTop(SelectionMarker, radius + Math.Sin(angle) * distance - SelectionMarker.Height / 2d);
    }

    private void UpdateSelectedColor()
    {
        Color color = HsvToRgb(_hue, _saturation, _brightness);
        SelectedHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        HexTextBox.Text = SelectedHex;
        SelectedColorPreview.Background = new SolidColorBrush(color);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static Color? ParseColor(string value)
    {
        string hex = value.Trim().TrimStart('#');
        return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb)
            ? Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : null;
    }

    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double sector = hue / 60d;
        double x = chroma * (1d - Math.Abs(sector % 2d - 1d));
        (double r, double g, double b) = sector switch
        {
            < 1d => (chroma, x, 0d),
            < 2d => (x, chroma, 0d),
            < 3d => (0d, chroma, x),
            < 4d => (0d, x, chroma),
            < 5d => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        double m = value - chroma;
        return Color.FromRgb((byte)Math.Round((r + m) * 255d), (byte)Math.Round((g + m) * 255d), (byte)Math.Round((b + m) * 255d));
    }

    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        hue = delta == 0d ? 0d : max == r ? 60d * (((g - b) / delta) % 6d) : max == g ? 60d * ((b - r) / delta + 2d) : 60d * ((r - g) / delta + 4d);
        if (hue < 0d) hue += 360d;
        saturation = max == 0d ? 0d : delta / max;
        value = max;
    }
}
