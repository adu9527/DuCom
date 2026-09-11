using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DuCom.Plugin;
using DuCom.PluginHost;
using Wpf.Ui.Controls;

namespace DuCom.Services.Plugins;

public sealed record PluginMenuEntry(string Header, string PluginId, string? PageId, string CommandId);

/// <summary>
/// Marshals plugin runtime callbacks onto the WPF UI thread: publication and updates of
/// declarative contributions, plugin menu entries, tool-page windows, settings panels, and
/// the aggregated non-modal fault notices (exactly one row per failed activation).
/// </summary>
public sealed class PluginUiDispatcher
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PluginPublishedActivation> _activations = new(StringComparer.Ordinal);
    private readonly Dictionary<(string PluginId, string ContributionId), ToolPageContribution> _toolPages = new();
    private readonly Dictionary<string, List<ToolPageContribution>> _toolPagesByPlugin = new(StringComparer.Ordinal);
    private readonly ObservableCollection<HostFaultNotice> _faultNotices = [];
    private PluginFaultNoticeWindow? _faultWindow;

    public event EventHandler? Changed;

    public IReadOnlyList<PluginPublishedActivation> Activations
    {
        get
        {
            lock (_gate)
            {
                return [.. _activations.Values];
            }
        }
    }

    public IReadOnlyList<HostFaultNotice> FaultNotices => _faultNotices;

    public void PublishActivation(PluginPublishedActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        RunOnUi(() =>
        {
            lock (_gate)
            {
                _activations[activation.PluginId] = activation;
                _toolPagesByPlugin.Remove(activation.PluginId);
                foreach (ToolPageContribution page in activation.ToolPages)
                {
                    _toolPages[(activation.PluginId, page.ContributionId)] = page;
                    if (!_toolPagesByPlugin.TryGetValue(activation.PluginId, out List<ToolPageContribution>? pages))
                    {
                        pages = [];
                        _toolPagesByPlugin[activation.PluginId] = pages;
                    }

                    pages.Add(page);
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void UpdateToolPage(string pluginId, string contributionId, IReadOnlyList<UiNode> nodes)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentException.ThrowIfNullOrEmpty(contributionId);
        // Update the authoritative cache before returning to the worker. Window refresh remains
        // UI-thread-bound, but callers that await an "open" command must not observe old nodes.
        lock (_gate)
        {
            if (_toolPages.TryGetValue((pluginId, contributionId), out ToolPageContribution? page))
                _toolPages[(pluginId, contributionId)] = page with { Nodes = nodes };
            if (_toolPagesByPlugin.TryGetValue(pluginId, out List<ToolPageContribution>? pages))
            {
                for (int index = 0; index < pages.Count; index++)
                {
                    if (pages[index].ContributionId == contributionId)
                    {
                        pages[index] = pages[index] with { Nodes = nodes };
                    }
                }
            }
        }

        RunOnUi(() =>
        {
            foreach (PluginToolWindow window in Application.Current?.Windows.OfType<PluginToolWindow>().ToList() ?? [])
            {
                window.Refresh(pluginId, contributionId, nodes);
            }

            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public void RemoveActivation(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        RunOnUi(() =>
        {
            lock (_gate)
            {
                _activations.Remove(pluginId);
                _toolPagesByPlugin.Remove(pluginId);
                foreach (((string owner, string contribution), _) in _toolPages.Where(pair => pair.Key.PluginId == pluginId).ToList())
                {
                    _toolPages.Remove((owner, contribution));
                }
            }

            foreach (PluginToolWindow window in Application.Current?.Windows.OfType<PluginToolWindow>()
                         .Where(window => string.Equals(window.PluginId, pluginId, StringComparison.Ordinal)).ToList() ?? [])
            {
                window.Close();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    public IReadOnlyList<PluginMenuEntry> BuildMenuEntries()
    {
        List<PluginMenuEntry> entries = [];
        lock (_gate)
        {
            foreach (PluginPublishedActivation activation in _activations.Values.OrderBy(item => item.PluginId, StringComparer.Ordinal))
            {
                foreach (MenuContribution menu in activation.Menus.OrderBy(menu => menu.Order))
                {
                    entries.Add(new PluginMenuEntry(menu.Label, activation.PluginId, menu.PageId, menu.CommandId));
                }
            }
        }

        return entries;
    }

    public async Task<bool> ShowFaultNoticeAsync(HostFaultNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Application? application = Application.Current;
        if (application is null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
        {
            return false;
        }

        try
        {
            return await application.Dispatcher.InvokeAsync(() =>
            {
                if (application.Dispatcher.HasShutdownStarted || application.MainWindow is not { IsVisible: true } owner
                    || owner.WindowState == WindowState.Minimized)
                {
                    return false;
                }

                _faultNotices.Add(notice);
                try
                {
                    if (_faultWindow is null)
                    {
                        _faultWindow = new PluginFaultNoticeWindow(_faultNotices) { Owner = owner };
                        _faultWindow.Closed += (_, _) =>
                        {
                            _faultWindow = null;
                            _faultNotices.Clear();
                        };
                    }

                    _faultWindow.Show();
                    bool shown = _faultWindow is { IsVisible: true, IsLoaded: true }
                        && !application.Dispatcher.HasShutdownStarted;
                    if (!shown)
                    {
                        _faultNotices.Remove(notice);
                    }
                    return shown;
                }
                catch
                {
                    _faultNotices.Remove(notice);
                    throw;
                }
            }).Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Aborted dispatch and failed Show must leave the persisted notice pending.
            return false;
        }
    }

    public void OpenToolPage(string pluginId, string contributionId, Func<string, IReadOnlyDictionary<string, string>, Task<bool>> commandInvoker)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        ArgumentException.ThrowIfNullOrEmpty(contributionId);
        ArgumentNullException.ThrowIfNull(commandInvoker);
        ToolPageContribution? page;
        PluginPublishedActivation? activation;
        lock (_gate)
        {
            if (!_toolPages.TryGetValue((pluginId, contributionId), out ToolPageContribution? found))
            {
                return;
            }

            page = found;
            _activations.TryGetValue(pluginId, out activation);
        }

        RunOnUi(() =>
        {
            foreach (PluginToolWindow existing in Application.Current.Windows.OfType<PluginToolWindow>()
                         .Where(window => string.Equals(window.PluginId, pluginId, StringComparison.Ordinal)
                             && string.Equals(window.ContributionId, contributionId, StringComparison.Ordinal)))
            {
                existing.Activate();
                return;
            }

            PluginToolWindow window = new(pluginId, page!, activation?.PluginName ?? pluginId, commandInvoker)
            {
                Owner = Application.Current?.MainWindow,
            };
            window.SetContent(page!.Nodes);
            window.Show();
        });
    }

    public IReadOnlyList<SettingsPanelContribution> GetSettingsPanels(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        lock (_gate)
        {
            return _activations.TryGetValue(pluginId, out PluginPublishedActivation? activation) ? activation.SettingsPanels : [];
        }
    }

    private static void RunOnUi(Action action)
    {
        Application? application = Application.Current;
        if (application is null)
        {
            return;
        }

        application.Dispatcher.BeginInvoke(action);
    }
}
