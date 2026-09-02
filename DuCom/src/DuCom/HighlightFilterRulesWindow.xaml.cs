using DuCom.Core.Parsing;
using DuCom.ViewModels;
using Wpf.Ui.Controls;

namespace DuCom;

public partial class HighlightFilterRulesWindow : FluentWindow
{
    public HighlightFilterRulesWindow(HighlightFilterRuleService service)
    {
        InitializeComponent();
        DataContext = new HighlightFilterRulesViewModel(service);
    }
}
