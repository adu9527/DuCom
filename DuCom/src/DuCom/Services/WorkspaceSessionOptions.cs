using DuCom.Core.Parsing;
using DuCom.Core.Ports;

namespace DuCom.Services;

internal sealed record WorkspaceSessionOptions(
    SerialPortSettings PortSettings,
    ReceiveDisplayMode ReceiveDisplayMode,
    bool TimestampEnabled,
    bool LoggingEnabled,
    string LogDirectory,
    long LogRotationBytes,
    bool LogRotationEnabled,
    int DisplayBudgetBytes,
    string LogFileNameFormat,
    bool SendPrefixEnabled,
    string SendPrefix,
    string TimestampFormat);
