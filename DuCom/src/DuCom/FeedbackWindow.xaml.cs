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

    private void CopyQQ_Click(object sender, RoutedEventArgs e) => Clipboard.SetText(QQGroupNumber);

    private void GitHub_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true });

    private void OpenSystemLog_Click(object sender, RoutedEventArgs e) => SystemLogAccess.OpenCurrent();

    private void TitleBar_CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
