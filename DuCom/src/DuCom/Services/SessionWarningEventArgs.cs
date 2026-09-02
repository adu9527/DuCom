namespace DuCom.Services;

public sealed class SessionWarningEventArgs(string warning) : EventArgs
{
    public string Warning { get; } = warning ?? throw new ArgumentNullException(nameof(warning));
}
