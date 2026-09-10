using DuCom.Core.Ports;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private void RememberPortOverride(string portName)
    {
        SessionViewModel? session = Sessions.FirstOrDefault(item =>
            string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase));
        SerialPortSettings? settings = session?.WorkspaceSession.Settings;
        _portOverrides.TryGetValue(portName, out PortSettingSnapshot? previous);
        Guid? highlightProjectId = session?.HighlightRuleProjectId ?? previous?.HighlightRuleProjectId;
        _portOverrides[portName] = new PortSettingSnapshot(
            settings?.BaudRate ?? previous?.BaudRate ?? BaudRate,
            settings?.DataBits ?? previous?.DataBits ?? DataBits,
            settings?.StopBits ?? previous?.StopBits ?? StopBits,
            settings?.Parity ?? previous?.Parity ?? Parity,
            settings?.Handshake ?? previous?.Handshake ?? Handshake,
            settings?.EncodingName ?? previous?.EncodingName ?? EncodingName,
            settings?.DtrEnable ?? previous?.DtrEnable,
            settings?.RtsEnable ?? previous?.RtsEnable,
            settings?.DiscardNull ?? previous?.DiscardNull,
            session?.SendMode ?? previous?.SendMode,
            session?.Newline ?? previous?.Newline,
            session?.ReceiveMode ?? previous?.ReceiveMode,
            session?.TimestampEnabled ?? previous?.TimestampEnabled,
            session?.LoggingEnabled ?? previous?.LoggingEnabled,
            LogDirectory,
            LogRotationMegabytes,
            LogRotationEnabled,
            DisplayBudgetMegabytes,
            LogFileNameFormat,
            SendPrefixEnabled,
            SendPrefix,
            session?.FollowEnd ?? previous?.FollowEnd,
            session?.FilterEnabled ?? previous?.FilterEnabled,
            AutoReconnect: session?.AutoReconnect ?? previous?.AutoReconnect,
            HighlightRuleProjectId: highlightProjectId,
            HighlightRuleChoiceMade: highlightProjectId is null &&
                (session is not null || previous?.HighlightRuleChoiceMade == true));
        MarkSettingsDirty();
    }

    internal void RememberSessionHighlightProject(SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);
        RememberPortOverride(session.PortName);
    }

    internal void ApplySessionHighlightProject(SessionViewModel session, Guid? projectId)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.ApplyHighlightRuleProject(projectId);
        RememberSessionHighlightProject(session);
    }

    private void RememberPortOverride(string portName, SerialPortSettings settings)
    {
        _portOverrides.TryGetValue(portName, out PortSettingSnapshot? previous);
        _portOverrides[portName] = new PortSettingSnapshot(
            settings.BaudRate,
            settings.DataBits,
            settings.StopBits,
            settings.Parity,
            settings.Handshake,
            settings.EncodingName,
            settings.DtrEnable,
            settings.RtsEnable,
            settings.DiscardNull,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.SendMode,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.Newline,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.ReceiveMode,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.TimestampEnabled,
            Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.LoggingEnabled,
            LogDirectory,
            LogRotationMegabytes,
            LogRotationEnabled,
            DisplayBudgetMegabytes,
            LogFileNameFormat,
            SendPrefixEnabled,
            SendPrefix,
            FollowEnd: Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.FollowEnd,
            FilterEnabled: Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.FilterEnabled,
            AutoReconnect: Sessions.FirstOrDefault(item => string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))?.AutoReconnect,
            HighlightRuleProjectId: previous?.HighlightRuleProjectId,
            HighlightRuleChoiceMade: previous?.HighlightRuleChoiceMade ?? false);
        MarkSettingsDirty();
    }
}
