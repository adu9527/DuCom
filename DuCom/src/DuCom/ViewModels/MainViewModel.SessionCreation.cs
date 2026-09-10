using DuCom.Core.Ports;
using DuCom.Core.Persistence;
using DuCom.Services;

namespace DuCom.ViewModels;

public partial class MainViewModel
{
    private SessionViewModel CreateClosedSession(string portName)
    {
        SerialPortSettings defaults = SerialPortSettings.Default(portName);
        _portOverrides.TryGetValue(portName, out PortSettingSnapshot? overrideValues);
        SerialPortSettings settings = defaults with
        {
            BaudRate = overrideValues?.BaudRate ?? BaudRate,
            DataBits = overrideValues?.DataBits ?? DataBits,
            StopBits = overrideValues?.StopBits ?? StopBits,
            Parity = overrideValues?.Parity ?? Parity,
            Handshake = overrideValues?.Handshake ?? Handshake,
            EncodingName = overrideValues?.EncodingName ?? EncodingName,
            DtrEnable = overrideValues?.DtrEnable ?? defaults.DtrEnable,
            RtsEnable = overrideValues?.RtsEnable ?? defaults.RtsEnable,
            DiscardNull = overrideValues?.DiscardNull ?? defaults.DiscardNull,
        };
        SessionViewModel session = CreateSession(settings, ResolvePortPreferences(portName));
        session.AutoReconnect = overrideValues?.AutoReconnect ?? false;
        return session;
    }

    private SessionViewModel CreateSession(SerialPortSettings settings, PortSessionPreferences preferences)
    {
        WorkspaceSessionOptions options = new(
            settings,
            preferences.ReceiveMode,
            preferences.TimestampEnabled,
            preferences.LoggingEnabled,
            preferences.LogDirectory,
            preferences.LogRotationBytes,
            preferences.LogRotationEnabled,
            preferences.DisplayBudgetBytes,
            preferences.LogFileNameFormat,
            preferences.SendPrefixEnabled,
            preferences.SendPrefix,
            preferences.TimestampFormat);
        return new SessionViewModel(
            _sessionFactory(options),
            preferences.ReceiveMode,
            preferences.TimestampEnabled,
            preferences.LoggingEnabled,
            preferences.SendMode,
            preferences.Newline,
            preferences.FollowEnd,
            preferences.FilterEnabled,
            HighlightRuleProjects,
            preferences.HighlightRuleProjectId);
    }

    private PortSessionPreferences ResolvePortPreferences(string portName)
    {
        _portOverrides.TryGetValue(portName, out PortSettingSnapshot? values);
        return new PortSessionPreferences(
            values?.ReceiveMode ?? ReceiveMode,
            values?.TimestampEnabled ?? TimestampEnabled,
            values?.LoggingEnabled ?? LoggingEnabled,
            LogDirectory,
            Math.Max(1, LogRotationMegabytes) * 1024L * 1024L,
            LogRotationEnabled,
            Math.Clamp(DisplayBudgetMegabytes, 16, 512) * 1024 * 1024,
            LogFileNameFormat,
            SendPrefixEnabled,
            SendPrefix,
            TimestampFormat,
            values?.FollowEnd ?? true,
            values?.FilterEnabled ?? true,
            values?.SendMode ?? DefaultSendMode,
            values?.Newline ?? DefaultNewline,
            values is { HighlightRuleChoiceMade: true } ? values.HighlightRuleProjectId
                : values?.HighlightRuleProjectId ?? HighlightRuleProjects.FirstOrDefault()?.Id);
    }

    private async Task<SessionViewModel> RebuildClosedSessionAsync(SessionViewModel session)
    {
        int sessionIndex = Sessions.IndexOf(session);
        int rightIndex = RightSessions.IndexOf(session);
        bool selected = ReferenceEquals(SelectedSession, session);
        bool selectedRight = ReferenceEquals(SelectedRightSession, session);
        SerialPortSettings settings = session.WorkspaceSession.Settings;
        CloseFloatSendFor(session.PortName); CloseLogFilterFor(session.PortName);
        if (rightIndex >= 0)
        {
            RightSessions.RemoveAt(rightIndex);
        }

        Sessions.RemoveAt(sessionIndex);
        await session.DisposeAsync();
        SessionViewModel replacement = CreateSession(settings, ResolvePortPreferences(settings.PortName));
        replacement.AutoReconnect = session.AutoReconnect;
        Sessions.Insert(sessionIndex, replacement);
        if (rightIndex >= 0)
        {
            replacement.IsInRightPane = true;
            RightSessions.Insert(Math.Min(rightIndex, RightSessions.Count), replacement);
        }

        if (selected)
        {
            SelectedSession = replacement;
        }
        if (selectedRight)
        {
            SelectedRightSession = replacement;
        }

        return replacement;
    }
}
