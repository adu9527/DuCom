using System.Windows;
using System.Windows.Threading;

namespace DuCom.App.Tests;

/// <summary>One live Application and STA for the test process; closing windows never ends it.</summary>
internal static class WpfTestDispatcher
{
    private static readonly Lazy<Dispatcher> Shared = new(() =>
    {
        TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.SetResult(app.Dispatcher);
                app.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "DuCom.App.Tests.WPF" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    });

    internal static void Run(Action test) => Shared.Value.Invoke(test);
}
