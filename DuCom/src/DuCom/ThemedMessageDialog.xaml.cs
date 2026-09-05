using System.Windows;
using Wpf.Ui.Controls;

namespace DuCom;

public enum ThemedMessageDialogKind
{
    Information,
    Warning,
    Error,
}

public partial class ThemedMessageDialog : FluentWindow
{
    private ThemedMessageDialog()
    {
        InitializeComponent();
    }

    public static bool Confirm(Window? owner, string message, string title)
        => Confirm(owner, message, title, "Dialog.Yes", "Dialog.No");

    public static bool Confirm(Window? owner, string message, string title, string primaryResourceKey, string secondaryResourceKey)
    {
        ThemedMessageDialog dialog = Create(owner, message, title, ThemedMessageDialogKind.Warning);
        dialog.PrimaryButton.Content = GetResourceString(primaryResourceKey, "Yes");
        dialog.SecondaryButton.Content = GetResourceString(secondaryResourceKey, "No");
        dialog.SecondaryButton.Visibility = Visibility.Visible;
        return dialog.ShowDialog() == true;
    }

    public static void Show(Window? owner, string message, string title, ThemedMessageDialogKind kind)
    {
        ThemedMessageDialog dialog = Create(owner, message, title, kind);
        dialog.PrimaryButton.Content = GetResourceString("Connection.KeepFailedTab", "Keep tab");
        dialog.SecondaryButton.Content = GetResourceString("Connection.CloseFailedTab", "Close tab");
        dialog.SecondaryButton.Visibility = Visibility.Visible;
        _ = dialog.ShowDialog();
    }

    public static bool ShowOpenFailure(Window? owner, string message, string title, object session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ThemedMessageDialog dialog = Create(owner, message, title, ThemedMessageDialogKind.Error);
        dialog.Tag = session;
        dialog.PrimaryButton.Content = GetResourceString("Dialog.OK", "OK");
        dialog.SecondaryButton.Visibility = Visibility.Collapsed;

        // The primary action keeps the failed session available for parameter changes.
        // The secondary action and title-bar X both request removing the failed tab.
        return dialog.ShowDialog() != true;
    }

    public static void CloseOpenFailureFor(object session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach (ThemedMessageDialog dialog in Application.Current.Windows.OfType<ThemedMessageDialog>().ToArray())
        {
            if (ReferenceEquals(dialog.Tag, session))
            {
                dialog.Close();
            }
        }
    }

    private static ThemedMessageDialog Create(Window? owner, string message, string title, ThemedMessageDialogKind kind)
    {
        ThemedMessageDialog dialog = new()
        {
            Owner = owner?.IsLoaded == true ? owner : Application.Current?.MainWindow,
            Title = title,
        };
        dialog.DialogTitleBar.Title = title;
        dialog.MessageText.Text = message;
        dialog.MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40);
        dialog.ApplyKind(kind);
        return dialog;
    }

    private void ApplyKind(ThemedMessageDialogKind kind)
    {
        switch (kind)
        {
            case ThemedMessageDialogKind.Error:
                IconSurface.Background = (System.Windows.Media.Brush)FindResource("Brush.DangerSoft");
                DialogIcon.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
                DialogIcon.Symbol = SymbolRegular.ErrorCircle24;
                break;
            case ThemedMessageDialogKind.Warning:
                IconSurface.Background = (System.Windows.Media.Brush)FindResource("Brush.CautionSoft");
                DialogIcon.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Caution");
                DialogIcon.Symbol = SymbolRegular.Warning24;
                break;
            default:
                IconSurface.Background = (System.Windows.Media.Brush)FindResource("Brush.AccentSoft");
                DialogIcon.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Accent");
                DialogIcon.Symbol = SymbolRegular.Info24;
                break;
        }
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string GetResourceString(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;
}
