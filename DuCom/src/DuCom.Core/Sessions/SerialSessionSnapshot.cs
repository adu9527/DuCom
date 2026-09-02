using DuCom.Core.Diagnostics;
using DuCom.Core.Ports;
using DuCom.Core.Storage;

namespace DuCom.Core.Sessions;

public sealed record SessionFaultSnapshot(string Source, string Message);

public sealed record SerialSessionSnapshot(
    PortLifecycleSnapshot State,
    LineStoreSnapshot Lines,
    PipelineMetricsSnapshot Metrics,
    SessionFaultSnapshot? Fault);

public sealed record SerialSessionStatusSnapshot(
    PortLifecycleSnapshot State,
    PipelineMetricsSnapshot Metrics,
    SessionFaultSnapshot? Fault);
