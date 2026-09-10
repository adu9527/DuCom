using System.Windows;

namespace DuCom.Services.Plugins;

public sealed class PluginCommandRouter
{
    private readonly Func<string, IReadOnlyDictionary<string, string>, Task<bool>> _invoker;

    internal DependencyObject? FormRoot { get; set; }

    public PluginCommandRouter(Func<string, IReadOnlyDictionary<string, string>, Task<bool>> invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        _invoker = invoker;
    }

    public void InvokeCommand(string commandId, bool submitForm)
    {
        IReadOnlyDictionary<string, string> values = submitForm && FormRoot is { } root
            ? PluginUiRenderer.CollectFormValues(root)
            : new Dictionary<string, string>();

        _ = _invoker(commandId, values);
    }
}
