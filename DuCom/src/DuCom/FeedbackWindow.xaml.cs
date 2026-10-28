using System.Diagnostics;
using System.Windows;
using DuCom.Services;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class FeedbackWindow : FluentWindow
{
    private const string GitHubUrl = "https://github.com/adu9527/DuCom";
    private const string QQGroupNumber = "1107820408";

    public FeedbackWindow()
    {
        InitializeComponent();
    }

    internal static FeedbackWindow ShowOrActivate()
    {
        FeedbackWindow? existing = Application.Current.Windows.OfType<FeedbackWindow>().FirstOrDefault();
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        FeedbackWindow window = new();
        window.Show();
        return window;
    }

    private void CopyQQ_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(QQGroupNumber);

    private void GitHub_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });

    private void OpenSystemLog_Click(object sender, RoutedEventArgs e) => SystemLogAccess.OpenCurrent();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
