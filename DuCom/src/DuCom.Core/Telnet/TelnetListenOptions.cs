using System.Net;

namespace DuCom.Core.Telnet;

/// <summary>
/// Bind policy for the Telnet bridge listener. The default is loopback-only; accepting
/// remote connections is an explicit opt-in that the UI must present with a bilingual
/// warning (an open bridge exposes the serial port to the local network).
/// </summary>
public sealed record TelnetListenOptions(int Port, bool AllowRemote = false, bool AuthenticationEnabled = false)
{
    public IPAddress BindAddress => AllowRemote ? IPAddress.Any : IPAddress.Loopback;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65_535);
        if (AllowRemote && !AuthenticationEnabled)
        {
            throw new InvalidOperationException("Remote Telnet listening requires authentication.");
        }
    }
}
