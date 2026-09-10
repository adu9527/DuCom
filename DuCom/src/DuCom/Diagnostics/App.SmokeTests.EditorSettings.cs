using System.Windows;
using DuCom.ViewModels;

namespace DuCom;

public partial class App
{
    private async void RunSettingsSmokeTest(Window owner)
    {
        try
        {
            SettingsWindow settings = new(selectedCategory: 1, transportOnly: true)
            {
                Owner = owner,
                DataContext = owner.DataContext,
            };
            settings.Show();
            await Task.Delay(300);
            if (!settings.IsVisible || settings.ActualWidth < settings.MinWidth || settings.ActualHeight < settings.MinHeight)
            {
                throw new InvalidOperationException("Settings window did not initialize at its minimum usable size.");
            }

            if (settings.DtrEnableCheckBox.Visibility != Visibility.Visible ||
                settings.RtsEnableCheckBox.Visibility != Visibility.Visible ||
                settings.DiscardNullCheckBox.Visibility != Visibility.Visible ||
                settings.DtrEnableCheckBox.Content is not string dtr || string.IsNullOrWhiteSpace(dtr) ||
                settings.RtsEnableCheckBox.Content is not string rts || string.IsNullOrWhiteSpace(rts) ||
                settings.DiscardNullCheckBox.Content is not string discardNull || string.IsNullOrWhiteSpace(discardNull))
            {
                throw new InvalidOperationException("Serial line-control settings are not visible or localized.");
            }

            settings.Close();

            SettingsWindow generalSettings = new(selectedCategory: 0)
            {
                Owner = owner,
                DataContext = owner.DataContext,
            };
            generalSettings.Show();
            await Task.Delay(200);
            if (generalSettings.PrivateMemoryMonitorCheckBox.Content is not string monitorLabel ||
                string.IsNullOrWhiteSpace(monitorLabel) ||
                generalSettings.PrivateMemoryThresholdTextBox.Text.Length == 0)
            {
                throw new InvalidOperationException("Private-memory monitor settings are not visible or localized.");
            }

            if (owner.DataContext is not MainViewModel mainViewModel || !mainViewModel.HasPrivateMemoryMonitor)
            {
                throw new InvalidOperationException("Application-owned private-memory monitor is not attached.");
            }

            generalSettings.Close();
            DiagnosticLog?.Information("Settings smoke test passed.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Settings smoke test failed.", exception);
            Shutdown(-6);
        }
    }

    private async void RunEditorSmokeTest(Window owner)
    {
        try
        {
            System.Collections.ObjectModel.ObservableCollection<LogLineViewModel> lines = [];
            for (int index = 0; index < 200; index++)
            {
                string text = $"existing-{index:D4}";
                lines.Add(new LogLineViewModel(
                    index + 1,
                    0,
                    DateTimeOffset.UtcNow,
                    DuCom.Core.Storage.LineDirection.Rx,
                    text,
                    [new DuCom.Core.Parsing.StyleRun(text, null, null, null, null, null, null, false, false, false)]));
            }

            Controls.BoundedLogEditor editor = new()
            {
                Lines = lines,
                FollowEnd = true,
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Black,
            };
            Window probe = new()
            {
                Owner = owner,
                Width = 640,
                Height = 420,
                Content = editor,
                ShowInTaskbar = false,
            };
            probe.Show();
            await Task.Delay(250);
            if (!editor.IsReadOnly || !editor.Document.Text.Contains("existing-0199", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit did not project the existing log snapshot.");
            }

            System.Collections.ObjectModel.ObservableCollection<LogLineViewModel> initiallyPausedLines = [];
            string initiallyPausedText = "initially-paused";
            initiallyPausedLines.Add(new LogLineViewModel(
                1,
                0,
                DateTimeOffset.UtcNow,
                DuCom.Core.Storage.LineDirection.Rx,
                initiallyPausedText,
                [new DuCom.Core.Parsing.StyleRun(initiallyPausedText, null, null, null, null, null, null, false, false, false)]));
            Controls.BoundedLogEditor initiallyPausedEditor = new()
            {
                Lines = initiallyPausedLines,
                FollowEnd = false,
                Foreground = System.Windows.Media.Brushes.White,
                Background = System.Windows.Media.Brushes.Black,
            };
            Window initiallyPausedProbe = new()
            {
                Owner = owner,
                Width = 480,
                Height = 240,
                Content = initiallyPausedEditor,
                ShowInTaskbar = false,
            };
            initiallyPausedProbe.Show();
            await Task.Delay(200);
            if (!initiallyPausedEditor.Document.Text.Contains(initiallyPausedText, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit did not project initial content while follow mode was disabled.");
            }
            initiallyPausedProbe.Close();

            for (int index = 0; index < 300; index++)
            {
                string text = $"live-{index:D4}";
                lines.Add(new LogLineViewModel(
                    201 + index,
                    0,
                    DateTimeOffset.UtcNow,
                    DuCom.Core.Storage.LineDirection.Rx,
                    text,
                    [new DuCom.Core.Parsing.StyleRun(text, null, null, null, null, null, null, false, false, false)]));
            }
            for (int index = 0; index < 120; index++)
            {
                lines.RemoveAt(0);
            }

            await Task.Delay(250);
            if (!editor.Document.Text.Contains("live-0299", StringComparison.Ordinal) ||
                editor.Document.Text.Contains("existing-0000", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit did not apply live append and prefix eviction correctly.");
            }

            int selectionStart = editor.Document.Text.IndexOf("live-0299", StringComparison.Ordinal);
            editor.Select(selectionStart, "live-0299".Length);
            if (!string.Equals(editor.SelectedText, "live-0299", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit native text selection is not operational.");
            }

            editor.PauseFollow();
            editor.FollowEnd = false;
            double pausedOffset = editor.VerticalOffset;
            double pausedExtent = editor.ExtentHeight;
            for (int index = 0; index < 100; index++)
            {
                string text = $"paused-{index:D4}";
                lines.Add(new LogLineViewModel(
                    501 + index,
                    0,
                    DateTimeOffset.UtcNow,
                    DuCom.Core.Storage.LineDirection.Rx,
                    text,
                    [new DuCom.Core.Parsing.StyleRun(text, null, null, null, null, null, null, false, false, false)]));
            }
            await Task.Delay(250);
            if (Math.Abs(editor.VerticalOffset - pausedOffset) > 0.1d)
            {
                throw new InvalidOperationException("AvalonEdit continued following the end after follow mode was disabled.");
            }

            if (!editor.Document.Text.Contains("paused-0099", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("AvalonEdit stopped updating the document while follow mode was disabled.");
            }
            if (editor.ExtentHeight <= pausedExtent)
            {
                throw new InvalidOperationException("AvalonEdit did not update the scrollbar extent while follow mode was disabled.");
            }

            editor.FollowEnd = true;
            editor.ResumeFollow();
            await Task.Delay(250);

            probe.Close();
            DiagnosticLog?.Information("AvalonEdit log smoke test passed.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("AvalonEdit log smoke test failed.", exception);
            Shutdown(-7);
        }
    }
}
