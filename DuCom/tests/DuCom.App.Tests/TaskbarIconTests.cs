using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DuCom.Services;
using Xunit;

namespace DuCom.App.Tests;

public sealed class TaskbarIconTests
{
    [Fact]
    public void DynamicWindowHasNativeSmallAndLargeIconsAcrossTaskbarVisibilityChanges()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                TaskbarIconService.Initialize(portable: false);
                Window window = new() { Title = "DuCom icon test", Width = 300, Height = 180, ShowInTaskbar = true };
                TaskbarIconService.Attach(window);
                Assert.NotNull(window.Icon);
                window.Show();
                nint handle = new WindowInteropHelper(window).Handle;
                AssertIcons(handle);
                window.WindowState = WindowState.Minimized;
                window.WindowState = WindowState.Normal;
                window.ShowInTaskbar = false;
                window.ShowInTaskbar = true;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                AssertIcons(handle);
                SendMessage(handle, unchecked((int)RegisterWindowMessage("TaskbarButtonCreated")), 0, 0);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                AssertIcons(handle);
                window.Close();
                Window dynamicTool = new() { Width = 300, Height = 180 };
                dynamicTool.Show();
                dynamicTool.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                dynamicTool.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, dynamicTool));
                Assert.NotNull(dynamicTool.Icon);
                AssertIcons(new WindowInteropHelper(dynamicTool).Handle);
                dynamicTool.Close();
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void AssertIcons(nint hwnd)
    {
        Assert.NotEqual(nint.Zero, SendMessage(hwnd, 0x007F, 0, 0));
        Assert.NotEqual(nint.Zero, SendMessage(hwnd, 0x007F, 1, 0));
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
