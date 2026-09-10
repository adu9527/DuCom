using System.Windows;
using System.Windows.Controls;
using DuCom.Plugin;

namespace DuCom.Services.Plugins;

/// <summary>
/// Renders the bounded declarative UI tree contributed by plugins. The host owns every
/// control; plugin code never provides XAML, converters, or assemblies. Node counts, depth,
/// list lengths, and text lengths were validated by UiContributionValidator before this
/// renderer sees them.
/// </summary>
public static partial class PluginUiRenderer
{
    public static FrameworkElement Render(IReadOnlyList<UiNode> nodes, PluginCommandRouter commands, string? pluginId = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(commands);
        if (string.Equals(pluginId, "com.ducom.log-package", StringComparison.Ordinal))
        {
            return RenderLogPackage(nodes, commands);
        }

        if (string.Equals(pluginId, "com.ducom.background-image", StringComparison.Ordinal))
        {
            return RenderBackgroundImage(nodes, commands);
        }

        if (string.Equals(pluginId, "com.ducom.timer", StringComparison.Ordinal))
        {
            return RenderStopwatch(nodes, commands);
        }

        if (string.Equals(pluginId, "com.ducom.timer", StringComparison.Ordinal))
        {
            return RenderTimer(nodes, commands);
        }

        Panel root = MakePanel(UiDirection.Vertical);
        foreach (FrameworkElement element in nodes.Select(node => RenderNode(node, commands)))
        {
            root.Children.Add(element);
        }

        commands.FormRoot = root;
        ApplyTheme(root);
        return root;
    }
}
