using System.Windows;

namespace DuCom;

public partial class LogMessageDetailsWindow : Window
{
    public LogMessageDetailsWindow(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageBox.Text = message;
        Loaded += (_, _) => MessageBox.Focus();
    }

    public void ShowMessage(string title, string message)
    {
        Title = title;
        MessageBox.Text = message;
        MessageBox.SelectAll();
        MessageBox.Focus();
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MessageBox.SelectAll();
            MessageBox.Copy();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning("Failed to copy log analyzer message.", exception);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
