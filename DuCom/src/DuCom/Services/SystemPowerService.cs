using System.Runtime.InteropServices;

namespace DuCom.Services;

internal static class SystemPowerService
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    internal static void SetPreventSleep(bool enabled)
    {
        uint state = enabled
            ? EsContinuous | EsSystemRequired | EsDisplayRequired
            : EsContinuous;
        _ = SetThreadExecutionState(state);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint executionState);
}
