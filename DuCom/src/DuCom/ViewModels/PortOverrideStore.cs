using DuCom.Core.Ports;
using DuCom.Services;

namespace DuCom.ViewModels;

internal sealed class PortOverrideStore
{
    private Dictionary<string, PortSettingSnapshot> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Replace(IReadOnlyDictionary<string, PortSettingSnapshot> values) =>
        _values = new Dictionary<string, PortSettingSnapshot>(values, StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string portName, out PortSettingSnapshot? value) => _values.TryGetValue(portName, out value);

    public SerialPortSettings ApplyTransport(string portName, SerialPortSettings defaults, ApplicationSessionDefaults application)
    {
        _values.TryGetValue(portName, out PortSettingSnapshot? value);
        return defaults with
        {
            BaudRate = value?.BaudRate ?? application.BaudRate,
            DataBits = value?.DataBits ?? application.DataBits,
            StopBits = value?.StopBits ?? application.StopBits,
            Parity = value?.Parity ?? application.Parity,
            Handshake = value?.Handshake ?? application.Handshake,
            EncodingName = value?.EncodingName ?? application.EncodingName,
            DtrEnable = value?.DtrEnable ?? defaults.DtrEnable,
            RtsEnable = value?.RtsEnable ?? defaults.RtsEnable,
            DiscardNull = value?.DiscardNull ?? defaults.DiscardNull,
        };
    }

    public void Remember(string portName, SerialPortSettings? settings, SessionViewModel? session, ApplicationSessionDefaults application)
    {
        _values.TryGetValue(portName, out PortSettingSnapshot? previous);
        Guid? projectId = session?.HighlightRuleProjectId ?? previous?.HighlightRuleProjectId;
        _values[portName] = new PortSettingSnapshot(
            settings?.BaudRate ?? previous?.BaudRate ?? application.BaudRate,
            settings?.DataBits ?? previous?.DataBits ?? application.DataBits,
            settings?.StopBits ?? previous?.StopBits ?? application.StopBits,
            settings?.Parity ?? previous?.Parity ?? application.Parity,
            settings?.Handshake ?? previous?.Handshake ?? application.Handshake,
            settings?.EncodingName ?? previous?.EncodingName ?? application.EncodingName,
            settings?.DtrEnable ?? previous?.DtrEnable,
            settings?.RtsEnable ?? previous?.RtsEnable,
            settings?.DiscardNull ?? previous?.DiscardNull,
            session?.SendMode ?? previous?.SendMode,
            session?.Newline ?? previous?.Newline,
            session?.ReceiveMode ?? previous?.ReceiveMode,
            session?.TimestampEnabled ?? previous?.TimestampEnabled,
            session?.LoggingEnabled ?? previous?.LoggingEnabled,
            application.LogDirectory,
            application.LogRotationMegabytes,
            application.LogRotationEnabled,
            application.DisplayBudgetMegabytes,
            application.LogFileNameFormat,
            application.SendPrefixEnabled,
            application.SendPrefix,
            session?.FollowEnd ?? previous?.FollowEnd,
            session?.FilterEnabled ?? previous?.FilterEnabled,
            session?.AutoReconnect ?? previous?.AutoReconnect,
            projectId,
            projectId is null && (session is not null || previous?.HighlightRuleChoiceMade == true));
    }

    public Dictionary<string, PortSettingSnapshot> Capture(
        IEnumerable<SessionViewModel> sessions,
        ApplicationSessionDefaults application)
    {
        Dictionary<string, PortSettingSnapshot> result = new(_values, StringComparer.OrdinalIgnoreCase);
        foreach (SessionViewModel session in sessions)
        {
            Remember(session.PortName, session.WorkspaceSession.Settings, session, application);
            result[session.PortName] = _values[session.PortName];
        }

        return result;
    }
}

internal sealed record ApplicationSessionDefaults(
    int BaudRate,
    int DataBits,
    System.IO.Ports.StopBits StopBits,
    System.IO.Ports.Parity Parity,
    System.IO.Ports.Handshake Handshake,
    string EncodingName,
    DuCom.Core.Parsing.ReceiveDisplayMode ReceiveMode,
    bool TimestampEnabled,
    bool LoggingEnabled,
    string LogDirectory,
    int LogRotationMegabytes,
    bool LogRotationEnabled,
    int DisplayBudgetMegabytes,
    string LogFileNameFormat,
    bool SendPrefixEnabled,
    string SendPrefix,
    string TimestampFormat,
    DuCom.Core.Sending.SendMode SendMode,
    DuCom.Core.Sending.NewlinePolicy Newline,
    bool FreezeAfterSend);
