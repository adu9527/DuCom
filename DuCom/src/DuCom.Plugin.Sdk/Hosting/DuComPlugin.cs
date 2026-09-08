using System.Text.Json;
using DuCom.Plugin.Dto;

namespace DuCom.Plugin;

public sealed record CommandInvokeOutcome(bool Accepted, string? TaskId = null, string? Message = null)
{
    public static CommandInvokeOutcome Accept(string taskId) => new(true, taskId);

    public static CommandInvokeOutcome Reject(string message) => new(false, null, message);

    public static CommandInvokeOutcome Complete(string? message = null) => new(true, null, message);
}

public sealed record SettingsApplyOutcome(bool Ok, string? Message = null)
{
    public static SettingsApplyOutcome Success => new(true);

    public static SettingsApplyOutcome Failure(string message) => new(false, message);
}

public sealed class PluginTaskContext(string taskId, CancellationToken cancellation)
{
    public string TaskId { get; } = taskId;

    public CancellationToken Token { get; private set; } = cancellation;

    internal Action<TaskProgressNotice>? ProgressReporter { get; set; }

    internal bool Completed { get; private set; }

    public void ThrowIfCancellationRequested() => Token.ThrowIfCancellationRequested();

    public void ReportProgress(int? percent = null, string? message = null, long? processedBytes = null)
    {
        if (Completed)
        {
            return;
        }

        ProgressReporter?.Invoke(new TaskProgressNotice
        {
            TaskId = TaskId,
            Percent = percent is null ? null : Math.Clamp(percent.Value, 0, 100),
            Message = message,
            ProcessedBytes = processedBytes,
        });
    }

    internal void Complete() => Completed = true;

    internal void Cancel() => Token = new CancellationToken(canceled: true);
}

public abstract class DuComPlugin
{
    protected IPluginHostApi Api { get; private set; } = null!;

    public string PluginId => Api?.PluginId ?? string.Empty;

    protected internal void BindHostApi(IPluginHostApi api) => Api = api;

    public virtual Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken) => Task.FromResult(PluginActivation.Empty);

    public virtual Task DeactivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public virtual Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken) =>
        Task.FromResult(CommandInvokeOutcome.Reject($"Command '{commandId}' is not handled."));

    public virtual Task<SettingsApplyOutcome> OnSettingsApplyAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) =>
        Task.FromResult(SettingsApplyOutcome.Success);

    public virtual void OnSerialData(SerialDataEventArgs eventArgs)
    {
    }

    public virtual void OnSessionClosed(string sessionId)
    {
    }

    public void StartTask(string taskId, Func<PluginTaskContext, Task> taskBody)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        ArgumentNullException.ThrowIfNull(taskBody);
        Runner?.StartTask(this, taskId, taskBody);
    }

    public bool CancelTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        return Runner?.CancelPluginTask(taskId) ?? false;
    }

    internal PluginWorkerRunner? Runner { get; set; }
}
