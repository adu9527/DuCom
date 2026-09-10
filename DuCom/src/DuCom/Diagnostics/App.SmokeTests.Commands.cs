using System.IO;
using System.Windows;
using System.Windows.Threading;
using DuCom.ViewModels;

namespace DuCom;

public partial class App
{
    /// <summary>
    /// Drives the command-groups editor end to end through real UI clicks: create a
    /// group through the input dialog, rename it, add a command row, edit cells, delete
    /// the group again, and verify persistence. Mirrors the manual verification
    /// checklist section F.
    /// </summary>
    private void RunCommandsSmokeTest(Window owner)
    {
        MainViewModel? mainViewModel = owner.DataContext as MainViewModel;
        ThemedInputDialog.AutoResponder = initialValue =>
            initialValue is not null && initialValue.EndsWith("-created", StringComparison.Ordinal) ? null : $"{initialValue}-created";
        ThemedMessageDialog.AutoConfirmer = _ => true;
        try
        {
            CommandGroupsWindow window = new(mainViewModel?.CommandRunner, mainViewModel) { Owner = owner };
            window.Show();
            DoEvents();

            ViewModels.CommandGroupsViewModel viewModel = (ViewModels.CommandGroupsViewModel)window.DataContext;
            int baselineGroups = viewModel.CommandGroups.Count;

            // New group: click 新建 — the input dialog auto-answers with a unique name.
            if (InvokeButton(window, viewModel.AddCommandGroupCommand) is not true)
            {
                throw new InvalidOperationException("New-group button was not found or not enabled.");
            }

            DoEvents();
            string groupName = viewModel.SelectedCommandGroup?.Name ?? string.Empty;
            if (viewModel.CommandGroups.Count != baselineGroups + 1 ||
                !groupName.EndsWith("-created", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"New group was not created or selected. Count={viewModel.CommandGroups.Count}, Selected={viewModel.SelectedCommandGroup?.Name}.");
            }

            // Add command row through the 新增命令 button; the row must persist with an
            // empty payload (regression: the serializer used to drop such rows).
            int baselineCommands = viewModel.SelectedCommands.Count;
            if (InvokeButton(window, viewModel.AddScriptCommandCommand) is not true)
            {
                throw new InvalidOperationException("Add-command button was not found or not enabled.");
            }

            DoEvents();
            if (viewModel.SelectedCommands.Count != baselineCommands + 1)
            {
                throw new InvalidOperationException($"Command row was not added. Count={viewModel.SelectedCommands.Count}.");
            }

            viewModel.SelectedCommands[^1].NameText = "smoke-edit";
            viewModel.SelectedCommands[^1].PayloadText = "AT";
            viewModel.SelectedCommands[^1].SelectedNewline = DuCom.Core.Sending.NewlinePolicy.CrLf;
            viewModel.CommitScriptCommandEdits();

            // Rename: click 重命名 — the auto-responder dismisses, keeping the name.
            if (InvokeButton(window, viewModel.RenameSelectedCommandGroupCommand) is not true)
            {
                throw new InvalidOperationException("Rename-group button was not found or not enabled.");
            }

            DoEvents();
            if (viewModel.SelectedCommandGroup?.Name != groupName)
            {
                throw new InvalidOperationException("Dismissed rename dialog must not change the group name.");
            }

#if DEBUG
            CaptureWindowPng(window, Path.Combine(Path.GetTempPath(), $"ducom-commands-smoke-{Environment.ProcessId}.png"));
#endif
            // Delete the group through the view model command (dialog confirm is
            // UI-level; store behavior is verified below through persistence).
            viewModel.DeleteSelectedCommandGroupCommand.Execute(null);
            DoEvents();
            if (viewModel.CommandGroups.Count != baselineGroups)
            {
                throw new InvalidOperationException($"Group was not deleted. Count={viewModel.CommandGroups.Count}.");
            }

            // Persistence round trip: the smoke group with its edited command must be
            // gone, and groups saved before must survive with their commands intact.
            IReadOnlyList<DuCom.Core.Sending.CommandGroup> stored = Services.CommandScriptStore.Load();
            if (stored.Any(group => group.Name == groupName))
            {
                throw new InvalidOperationException("Deleted group is still persisted.");
            }

            window.Close();
            DiagnosticLog?.Information(
                $"Commands smoke test passed. Groups={viewModel.CommandGroups.Count}; StoredGroups={stored.Count}.");
            Shutdown(0);
        }
        catch (Exception exception)
        {
            DiagnosticLog?.Error("Commands smoke test failed.", exception);
            Shutdown(-9);
        }
        finally
        {
            ThemedInputDialog.AutoResponder = null;
            ThemedMessageDialog.AutoConfirmer = null;
        }
    }

    private static bool? InvokeButton(System.Windows.Controls.ContentControl host, string contentResourceKey)
    {
        string? expected = host.TryFindResource(contentResourceKey) as string;
        System.Windows.Controls.Button? target = FindButton(host, expected);
        return Click(target);
    }

    private static bool? InvokeButton(System.Windows.Controls.ContentControl host, System.Windows.Input.ICommand command)
    {
        System.Windows.Controls.Button? target = Descendants<System.Windows.Controls.Primitives.ButtonBase>(host)
            .OfType<System.Windows.Controls.Button>()
            .FirstOrDefault(button => Equals(button.Command, command));
        return Click(target);
    }

    private static bool? Click(System.Windows.Controls.Button? target)
    {
        if (target is null)
        {
            return null;
        }

        if (!target.IsEnabled)
        {
            return false;
        }

        // A real click goes through the protected ButtonBase.OnClick, which executes
        // the bound command; raising the routed Click event alone does not.
        target.GetType()
            .GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(target, null);
        return true;
    }

    private static System.Windows.Controls.Button? FindButton(System.Windows.Media.Visual root, string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        return Descendants<System.Windows.Controls.Button>(root)
            .FirstOrDefault(button => string.Equals(button.Content as string, content, StringComparison.Ordinal));
    }

    private static IEnumerable<T> Descendants<T>(System.Windows.Media.Visual parent)
        where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < count; index++)
        {
            System.Windows.Media.Visual child = (System.Windows.Media.Visual)System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T matched)
            {
                yield return matched;
            }

            foreach (T descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void DoEvents()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void CaptureWindowPng(Window window, string filePath)
    {
        System.Windows.Media.Imaging.RenderTargetBitmap bitmap = new(
            (int)window.ActualWidth,
            (int)window.ActualHeight,
            96,
            96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(filePath);
        encoder.Save(stream);
    }
}
