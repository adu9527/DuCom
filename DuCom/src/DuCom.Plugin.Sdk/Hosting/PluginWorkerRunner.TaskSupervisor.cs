using DuCom.Plugin.Dto;

namespace DuCom.Plugin;

public sealed partial class PluginWorkerRunner
{
    private async Task NotifyProgressSafeAsync(TaskProgressNotice notice)
    {
        try
        {
            await NotifyAsync(PluginOps.TaskProgress, notice, CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    internal void StartTask(DuComPlugin plugin, string taskId, Func<PluginTaskContext, Task> taskBody)
    {
        lock (_taskGate)
        {
            if (_tasks.ContainsKey(taskId))
            {
                throw new ArgumentException($"Task '{taskId}' already exists.");
            }

            CancellationTokenSource cts = new();
            PluginTaskContext context = new(taskId, cts.Token)
            {
                ProgressReporter = notice =>
                {
                    _ = NotifyProgressSafeAsync(notice);
                },
            };
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(_startup.Limits.TaskDeadlineMs, 1000));
            _tasks[taskId] = (context, cts, deadline);
            _ = RunTaskAsync(plugin, taskId, taskBody, context, cts, deadline);
        }
    }

    private async Task RunTaskAsync(DuComPlugin plugin, string taskId, Func<PluginTaskContext, Task> taskBody, PluginTaskContext context, CancellationTokenSource cts, DateTime deadlineUtc)
    {
        try
        {
            Task body = taskBody(context);
            Task expiry = Task.Delay(deadlineUtc - DateTime.UtcNow, cts.Token);
            Task finished = await Task.WhenAny(body, expiry);
            if (ReferenceEquals(finished, expiry))
            {
                cts.Cancel();
                try
                {
                    await body.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception)
                {
                }
            }
            else
            {
                await body;
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            context.Complete();
            cts.Dispose();
            lock (_taskGate)
            {
                _tasks.Remove(taskId);
            }
        }
    }

    internal bool CancelPluginTask(string taskId)
    {
        lock (_taskGate)
        {
            if (_tasks.TryGetValue(taskId, out (PluginTaskContext Context, CancellationTokenSource Cts, DateTime DeadlineUtc) entry))
            {
                entry.Context.Cancel();
                entry.Cts.Cancel();
                return true;
            }
        }

        return false;
    }

    private async Task CancelAllTasksAsync()
    {
        List<(PluginTaskContext Context, CancellationTokenSource Cts, DateTime DeadlineUtc)> entries;
        lock (_taskGate)
        {
            entries = [.. _tasks.Values];
        }

        foreach ((PluginTaskContext context, CancellationTokenSource cts, _) in entries)
        {
            context.Cancel();
            cts.Cancel();
        }
    }
}
