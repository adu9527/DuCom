using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DuCom.Core.Sending;
using DuCom.Services;
using Microsoft.Win32;

namespace DuCom;

public partial class FloatSendWindow
{
    private void RecvHexToggle_Click(object sender, RoutedEventArgs e)
    {
        _recvShowHex = RecvHexToggle.IsChecked == true;
    }

    private void SendHexToggle_Click(object sender, RoutedEventArgs e)
    {
        // Send mode is a per-port preference shared with the main workspace send bar.
        _session.SendMode = SendHexToggle.IsChecked == true ? SendMode.Hex : SendMode.Str;
        SavePreferences();
    }

    private void PinLogToggle_Click(object sender, RoutedEventArgs e)
    {
        _fixedLog = PinLogToggle.IsChecked == true;
        Behaviors.ListBoxAutoScrollBehavior.SetIsEnabled(LogList, !_fixedLog);
        if (!_fixedLog)
        {
            LogList.ScrollIntoView(Lines.Count > 0 ? Lines[^1] : null);
        }
    }

    private void TopmostToggle_Click(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostToggle.IsChecked == true;
        FloatSendGlobalPreferencesService.Save(new FloatSendGlobalPreferences(
            ReplyWindowMs: _replyWindowMs,
            Topmost: Topmost));
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        // Clears only the float window surface; the main session display is untouched.
        Lines.Clear();
        _tailLine = null;
        _lastSendAnchor = null;
        _bufferCharacters = 0;
        _lineBuffer.Clear();
        lock (_pendingGate)
        {
            _pendingText.Clear();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        string snapshot = string.Join(
            Environment.NewLine,
            Lines.Select(line => line.Text).Where(text => text.Length > 0));
        if (snapshot.Length == 0)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = (string?)TryFindResource("FloatSend.SaveFilter") ?? "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"DuCom-{PortName}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, snapshot + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Failed to save float send log. Port={PortName}.", exception);
            ThemedMessageDialog.Show(
                this,
                (string?)TryFindResource("FloatSend.SaveFailed") ?? "Failed to save the float send log.",
                Title,
                ThemedMessageDialogKind.Error);
        }
    }

    private void ReplyWindowBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = ReplyWindowBox.Text;
        foreach (char character in text)
        {
            if (!char.IsDigit(character))
            {
                ReplyWindowBox.Text = new string(text.Where(char.IsDigit).ToArray());
                ReplyWindowBox.CaretIndex = ReplyWindowBox.Text.Length;
                break;
            }
        }
    }

    private void ReplyWindowBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyReplyWindow();
            e.Handled = true;
        }
    }

    private void ReplyWindowBox_LostFocus(object sender, RoutedEventArgs e) => ApplyReplyWindow();

    private void ApplyReplyWindow()
    {
        if (!int.TryParse(ReplyWindowBox.Text, out int milliseconds))
        {
            ReplyWindowBox.Text = _replyWindowMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }

        milliseconds = Math.Clamp(
            milliseconds,
            FloatSendGlobalPreferencesService.MinimumReplyWindowMs,
            FloatSendGlobalPreferencesService.MaximumReplyWindowMs);
        _replyWindowMs = milliseconds;
        ReplyWindowBox.Text = milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        FloatSendGlobalPreferencesService.Save(new FloatSendGlobalPreferences(
            ReplyWindowMs: milliseconds,
            Topmost: Topmost));
        if (Application.Current.MainWindow is MainWindow owner)
        {
            owner.ApplyReplyWindowToFloatSends(this, milliseconds);
        }
    }

    internal void SetReplyWindowMs(int milliseconds)
    {
        _replyWindowMs = milliseconds;
        if (ReplyWindowBox.Text != milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            ReplyWindowBox.Text = milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void GroupsBox_DropOpened(object sender, EventArgs e)
    {
        // Re-read the command store so groups edited in the tool center show up immediately.
        Session.RefreshCommandGroups();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await DoSendAsync();

    private async void SendBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            await DoSendAsync();
            e.Handled = true;
        }
    }

    private async Task DoSendAsync()
    {
        string text = SendBox.Text;
        if (!_session.IsOpen || string.IsNullOrEmpty(text))
        {
            return;
        }

        // Fixed-scroll: remember the current last line so the reply jumps back to it.
        if (_fixedLog && Lines.Count > 0)
        {
            _lastSendAnchor = Lines[^1];
        }

        SendMode mode = SendHexToggle.IsChecked == true ? SendMode.Hex : SendMode.Str;
        NewlinePolicy newline = NewlineToggle.IsChecked == true ? NewlinePolicy.CrLf : NewlinePolicy.None;
        _session.SendMode = mode;
        _session.Newline = newline;
        try
        {
            await _session.WorkspaceSession.SendAsync(mode, text, newline).AsTask();
            SavePreferences();
        }
        catch (Exception exception)
        {
            Program.DiagnosticLog?.Warning($"Float send failed. Port={PortName}.", exception);
            ThemedMessageDialog.Show(
                this,
                (string?)TryFindResource("FloatSend.SendFailed") ?? "Send failed.",
                Title,
                ThemedMessageDialogKind.Error);
        }
    }
}
