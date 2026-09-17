using System.Reflection;
using DuCom.Controls;
using DuCom.Core.Parsing;
using DuCom.Core.Storage;
using DuCom.ViewModels;
using Xunit;

namespace DuCom.App.Tests;

public sealed class BoundedLogEditorProjectionTests
{
    [Fact]
    public void RepeatedPrefixTrimmingKeepsLatestTenThousandLinesAndSelection()
    {
        RunOnStaThread(() =>
        {
            BoundedLogEditor editor = new();
            List<LogLineViewModel> lines = CreateLines(0, 10_000);
            Synchronize(editor, lines);

            int selectedLine = 5_000;
            int selectedOffset = GetLineOffset(lines, selectedLine);
            editor.Select(selectedOffset, lines[selectedLine].Text.Length);

            for (int batch = 0; batch < 20; batch++)
            {
                lines.RemoveRange(0, 128);
                lines.AddRange(CreateLines(10_000 + batch * 128, 128));
                Synchronize(editor, lines);
            }

            Assert.Equal(10_000, editor.Document.LineCount - 1);
            Assert.Equal(string.Concat(lines.Select(line => line.Text + Environment.NewLine)), editor.Text);
            Assert.Equal("line-005000", editor.SelectedText);

            LogLineViewModel tail = lines[^1];
            lines[^1] = tail with { Text = tail.Text + "-continued" };
            Synchronize(editor, lines);

            Assert.Equal(10_000, editor.Document.LineCount - 1);
            Assert.EndsWith(lines[^1].Text + Environment.NewLine, editor.Text, StringComparison.Ordinal);
            Assert.Equal("line-005000", editor.SelectedText);
        });
    }

    private static List<LogLineViewModel> CreateLines(int start, int count) =>
        Enumerable.Range(start, count)
            .Select(index => new LogLineViewModel(
                index,
                0,
                DateTimeOffset.UnixEpoch,
                LineDirection.Rx,
                $"line-{index:D6}",
                []))
            .ToList();

    private static int GetLineOffset(List<LogLineViewModel> lines, int index)
    {
        int offset = 0;
        for (int lineIndex = 0; lineIndex < index; lineIndex++)
        {
            offset += lines[lineIndex].Text.Length + Environment.NewLine.Length;
        }
        return offset;
    }

    private static void Synchronize(BoundedLogEditor editor, List<LogLineViewModel> lines)
    {
        MethodInfo method = typeof(BoundedLogEditor).GetMethod(
            "SynchronizeDocument",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SynchronizeDocument was not found.");
        method.Invoke(editor, [lines, null]);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
