using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void ClipboardToHex()
    {
        string text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = GetResourceString("Status.ClipboardEmpty");
            return;
        }

        Clipboard.SetText(DuCom.Core.Sending.HexRepresentation.ToHexText(Encoding.UTF8.GetBytes(text)));
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void ClipboardFromHex()
    {
        string text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = GetResourceString("Status.ClipboardEmpty");
            return;
        }

        if (!DuCom.Core.Sending.HexRepresentation.TryParseHexText(text, out byte[] bytes))
        {
            StatusMessage = GetResourceString("Status.InvalidHex").Replace("{0}", text.Trim(), StringComparison.Ordinal);
            return;
        }

        Clipboard.SetText(Encoding.UTF8.GetString(bytes));
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void ClipboardTimestampsToLocal()
    {
        string text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            StatusMessage = GetResourceString("Status.ClipboardEmpty");
            return;
        }

        Clipboard.SetText(DuCom.Core.Parsing.DisplayTextTransform.TimestampsToLocal(text));
        StatusMessage = string.Empty;
    }

    [RelayCommand]
    private void CopyVisibleLog()
    {
        if (SelectedSession is not null)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, SelectedSession.VisibleLines.Select(line => line.Text)));
        }
    }
}
