using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DuCom.ViewModels;

namespace DuCom;

public partial class SessionWorkspace : UserControl
{
    public SessionWorkspace()
    {
        InitializeComponent();
        DataContextChanged += OnWorkspaceDataContextChanged;
        LogEditor.FollowEndResumedFromBottom += OnLogEditorFollowResumedFromBottom;
    }

    private void OnLogEditorFollowResumedFromBottom(object? sender, EventArgs e)
    {
        if (DataContext is SessionViewModel { FollowEnd: false } session)
        {
            session.FollowEnd = true;
        }

        LogEditor.ResumeFollow();
    }

    private void OnWorkspaceDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SessionViewModel oldSession)
        {
            oldSession.Search.FocusRequested -= OnSearchFocusRequested;
        }

        if (e.NewValue is SessionViewModel newSession)
        {
            newSession.Search.FocusRequested += OnSearchFocusRequested;
        }
    }

    private void OnSearchFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(FocusSearchTextBox, System.Windows.Threading.DispatcherPriority.Render);

    private void FocusSearchTextBox()
    {
        if (SearchTextBox.IsEnabled && SearchTextBox.Visibility == Visibility.Visible)
        {
            SearchTextBox.Focus();
            SearchTextBox.SelectAll();
        }
    }

    internal void FocusSendEditor()
    {
        if (SendEditor.IsEnabled)
        {
            SendEditor.Focus();
            SendEditor.CaretIndex = SendEditor.Text.Length;
        }
    }

    private void SendEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not SessionViewModel session || sender is not TextBox editor)
        {
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            int caret = editor.CaretIndex;
            editor.Text = editor.Text.Insert(caret, "\n");
            editor.CaretIndex = caret + 1;
            e.Handled = true;
        }
    }

    private void LogEditor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is SessionViewModel session && Window.GetWindow(this)?.DataContext is MainViewModel { PauseFollowOnMouseWheel: true } viewModel)
        {
            session.FollowEnd = false;
            viewModel.NotifyAutoScrollPaused();
        }
    }

    private void LogEditor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SessionViewModel session)
        {
            if (sender is Controls.BoundedLogEditor editor)
            {
                editor.PauseFollow();
            }
            session.FollowEnd = false;
            ActivateLogSession(session);
            if (Window.GetWindow(this)?.DataContext is MainViewModel viewModel)
            {
                viewModel.NotifyAutoScrollPaused();
            }
        }
    }

    private void FollowEndToggle_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionViewModel session || sender is not ToggleButton toggle)
        {
            return;
        }

        bool follow = toggle.IsChecked == true;
        session.FollowEnd = follow;
        if (follow)
        {
            LogEditor.ResumeFollow();
        }
        else
        {
            LogEditor.PauseFollow();
        }
    }

    private void LogEditor_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is not SessionViewModel session)
        {
            return;
        }

        ActivateLogSession(session);
        if (Window.GetWindow(this)?.DataContext is MainViewModel { PauseFollowOnFocus: true } viewModel)
        {
            session.FollowEnd = false;
            viewModel.NotifyAutoScrollPaused();
        }
    }

    private void ActivateLogSession(SessionViewModel session)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel viewModel)
        {
            viewModel.ActivateLogSession(session);
        }
    }

    private async void BaudRate_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || DataContext is not SessionViewModel session ||
            sender is not ComboBox { SelectedItem: int baudRate } ||
            Window.GetWindow(this)?.DataContext is not MainViewModel viewModel)
        {
            return;
        }

        await viewModel.ApplySessionBaudRateAsync(session, baudRate);
    }

    private void BaudRateBox_Loaded(object sender, RoutedEventArgs e)
    {
        SetBaudRateBoxValue(sender as ComboBox);
    }

    private void BaudRateBox_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SetBaudRateBoxValue(sender as ComboBox);

    private void SetBaudRateBoxValue(ComboBox? baudRateBox)
    {
        if (DataContext is SessionViewModel session && baudRateBox is not null)
        {
            if (!baudRateBox.Items.Contains(session.BaudRate))
            {
                baudRateBox.Items.Refresh();
            }

            baudRateBox.SelectedItem = session.BaudRate;
        }
    }

    private void HighlightRuleProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || DataContext is not SessionViewModel session ||
            Window.GetWindow(this)?.DataContext is not MainViewModel viewModel)
        {
            return;
        }

        viewModel.RememberSessionHighlightProject(session);
    }

    private void CommandMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button ||
            DataContext is not SessionViewModel session ||
            Window.GetWindow(this)?.DataContext is not MainViewModel viewModel)
        {
            return;
        }

        // Re-read the command store before showing the unified project/action menu.
        session.RefreshCommandGroups();
        menu.Items.Clear();
        MenuItem highlightRulesItem = new()
        {
            Header = Application.Current.TryFindResource("HighlightFilter.Menu") as string ?? "Highlight rules",
        };
        DuCom.Core.Parsing.HighlightFilterRuleProject? selectedHighlightProject = session.HighlightRuleProjects.FirstOrDefault(project => project.Id == session.HighlightRuleProjectId);
        if (selectedHighlightProject is not null)
        {
            highlightRulesItem.Header = $"{highlightRulesItem.Header}: {selectedHighlightProject.Name}";
        }
        else
        {
            highlightRulesItem.Header = $"{highlightRulesItem.Header}: {Application.Current.TryFindResource("HighlightFilter.None") as string ?? "None"}";
        }
        MenuItem noHighlightRulesItem = new()
        {
            Header = Application.Current.TryFindResource("HighlightFilter.None") as string ?? "None",
            IsCheckable = true,
            IsChecked = session.HighlightRuleProjectId is null,
        };
        noHighlightRulesItem.Click += (_, _) =>
        {
            viewModel.ActivateLogSession(session);
            viewModel.ApplySessionHighlightProject(session, null);
        };
        highlightRulesItem.Items.Add(noHighlightRulesItem);
        highlightRulesItem.Items.Add(new Separator());
        foreach (DuCom.Core.Parsing.HighlightFilterRuleProject project in session.HighlightRuleProjects)
        {
            MenuItem projectItem = new()
            {
                Header = project.Name,
                IsCheckable = true,
                IsChecked = session.HighlightRuleProjectId == project.Id,
            };
            projectItem.Click += (_, _) =>
            {
                viewModel.ActivateLogSession(session);
                viewModel.ApplySessionHighlightProject(session, project.Id);
            };
            highlightRulesItem.Items.Add(projectItem);
        }
        highlightRulesItem.Items.Add(new Separator());
        MenuItem manageHighlightRules = new()
        {
            Header = Application.Current.TryFindResource("HighlightFilter.Manage") as string ?? "Manage rules",
        };
        manageHighlightRules.Click += (_, _) => viewModel.OpenSettingsCategory(3);
        highlightRulesItem.Items.Add(manageHighlightRules);
        menu.Items.Add(highlightRulesItem);
        menu.Items.Add(new Separator());
        foreach (DuCom.Core.Sending.CommandGroup group in session.CommandGroups)
        {
            MenuItem groupItem = new()
            {
                Header = group.Name,
                IsCheckable = true,
                IsChecked = ReferenceEquals(group, session.SelectedCommandGroup),
            };
            groupItem.Click += (_, _) => session.SelectedCommandGroup = group;
            foreach (DuCom.Core.Sending.ScriptCommand command in group.OrderedCommands())
            {
                MenuItem commandItem = new()
                {
                    Header = command.Name,
                    ToolTip = command.Payload,
                };
                commandItem.Click += async (_, _) =>
                {
                    session.SelectedCommandGroup = group;
                    await session.SendScriptCommandCommand.ExecuteAsync(command);
                };
                groupItem.Items.Add(commandItem);
            }

            menu.Items.Add(groupItem);
        }

        menu.Items.Add(new Separator());
        MenuItem editItem = new()
        {
            Header = Application.Current.TryFindResource("Commands.EditParameters") as string ?? "Edit command parameters",
        };
        editItem.Click += (_, _) => viewModel.ShowToolCenterCommand.Execute("commands");
        menu.Items.Add(editItem);

        MenuItem sendItem = new()
        {
            Header = Application.Current.TryFindResource("Send.Options") as string ?? "Send options",
        };
        sendItem.Click += (_, _) => viewModel.OpenSendOptionsCommand.Execute(session);
        menu.Items.Add(sendItem);

        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}
