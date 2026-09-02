namespace DuCom.Core.Telnet;

/// <summary>Runtime-only Telnet authentication configuration.</summary>
public sealed record TelnetAuthenticationOptions(bool Enabled, string Username, string Password)
{
    public static TelnetAuthenticationOptions Disabled { get; } = new(false, string.Empty, string.Empty);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(Password);
    }
}
