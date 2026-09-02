using System.Windows.Controls;
using System.Windows;
using DuCom.ViewModels;

namespace DuCom;

public partial class HighlightFilterRulesControl : UserControl
{
    public HighlightFilterRulesControl()
    {
        InitializeComponent();
    }

    private void ForegroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not HighlightFilterRulesViewModel viewModel)
        {
            return;
        }

        string? color = ColorWheelDialog.Pick(Window.GetWindow(this), viewModel.EditingForegroundHex);
        if (color is not null)
        {
            viewModel.EditingForegroundHex = color;
        }
    }
}
