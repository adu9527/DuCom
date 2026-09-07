using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class ThemedInputDialog : FluentWindow
{
    private Func<string, string?>? _validator;

    private ThemedInputDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Test hook: when set, <see cref="Prompt"/> returns the mapped value instead of
    /// opening the dialog, so smoke tests can drive the full group CRUD flow.
    /// </summary>
    internal static Func<string?, string?>? AutoResponder { get; set; }

    /// <summary>Shows a modal text prompt. Returns null when dismissed or cancelled.</summary>
    /// <param name="validator">Returns an error resource key for invalid input, or null to accept.</param>
    public static string? Prompt(
        Window? owner,
        string message,
        string title,
        string initialvalue,
        Func<string, string?>? validator = null,
        string? primaryResourceKey = null,
        string? secondaryResourceKey = null)
    {
        if (AutoResponder is not null)
        {
            return AutoResponder(initialvalue);
        }

        ThemedInputDialog dialog = new()
        {
            Owner = owner?.IsLoaded == true ? owner : Application.Current?.MainWindow,
            Title = title,
        };
        dialog.DialogTitleBar.Title = title;
        dialog.MessageText.Text = message;
        dialog.InputBox.Text = initialvalue;
        dialog._validator = validator;
        dialog.PrimaryButton.Content = GetResourceString(primaryResourceKey ?? "Dialog.OK", "OK");
        dialog.SecondaryButton.Content = GetResourceString(secondaryResourceKey ?? "Dialog.Cancel", "Cancel");
        dialog.Loaded += (_, _) =>
        {
            dialog.InputBox.Focus();
            dialog.InputBox.SelectAll();
        };
        bool? result = dialog.ShowDialog();
        return result == true ? dialog.InputBox.Text.Trim() : null;
    }

    private bool Validate()
    {
        if (_validator is null)
        {
            return true;
        }

        string? errorKey = _validator(InputBox.Text);
        if (errorKey is null)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            return true;
        }

        ErrorText.Text = GetResourceString(errorKey, errorKey);
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate())
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            PrimaryButton_Click(sender, e);
        }
    }

    private static string GetResourceString(string key, string fallback) =>
        Application.Current?.TryFindResource(key) as string ?? fallback;
}
