using DuCom.Core.Parsing;
using DuCom.Core.Sending;

namespace DuCom.ViewModels;

internal sealed record PortSessionPreferences(
    ReceiveDisplayMode ReceiveMode,
    bool TimestampEnabled,
    bool LoggingEnabled,
    string LogDirectory,
    long LogRotationBytes,
    bool LogRotationEnabled,
    int DisplayBudgetBytes,
    string LogFileNameFormat,
    bool SendPrefixEnabled,
    string SendPrefix,
    string TimestampFormat,
    bool FollowEnd,
    bool FilterEnabled,
    SendMode SendMode,
    NewlinePolicy Newline,
    Guid? HighlightRuleProjectId);
