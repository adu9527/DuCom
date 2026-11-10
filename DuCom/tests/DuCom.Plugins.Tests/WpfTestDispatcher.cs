using System.Windows;
using System.Windows.Threading;

namespace DuCom.Plugins.Tests;

/// <summary>One process-wide Application and live STA for all plugin UI tests.</summary>
internal static class WpfTestDispatcher
{
    private static readonly Lazy<Dispatcher> Shared = new(Start);

    internal static void Run(Action test)
    {
        Dispatcher dispatcher = Shared.Value;
        if (dispatcher.CheckAccess()) { test(); return; }
        dispatcher.InvokeAsync(test, DispatcherPriority.Normal).Task
            .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    private static Dispatcher Start()
    {
        TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
                app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
                GC.KeepAlive(app);
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }) { IsBackground = true, Name = "DuCom plugin UI tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }
}
