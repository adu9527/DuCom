using System.Text;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.Timer;

public sealed class Plugin : DuComPlugin
{
    private const string PageId = "stopwatch";
    private static readonly TimeSpan ResetArmWindow = TimeSpan.FromSeconds(3);

    private readonly Lock _gate = new();
    private TimerSession _session = new();
    private TimerSession? _previous;
    private bool _resetArmed;
    private long _resetArmedAtUnixMs;
    private string _status = string.Empty;
    private volatile string? _activeTaskId;

    private string Status
    {
        set
        {
            lock (_gate)
            {
                _status = value;
            }

            _ = PushToolPageAsync();
        }
    }

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        string? json = await Api.Storage.ReadAsync(cancellationToken);
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        try
        {
            TimerPluginState? loaded = JsonSerializer.Deserialize<TimerPluginState>(json, TimerEngine.JsonOptions);
            if (loaded?.Current is { } current && current.Mode != StopwatchMode.Idle)
            {
                _session = current;
            }

            if (loaded?.Previous is { } previous && previous.Laps.Count > 0)
            {
                _previous = previous;
            }
        }
        catch (JsonException)
        {
        }
    }

    public override Task<PluginActivation> ActivateAsync(CancellationToken cancellationToken)
    {
        _ = PushToolPageAsync();
        return Task.FromResult(new PluginActivation
        {
            Menus =
            [
                new MenuContribution
                {
                    ContributionId = "open-stopwatch",
                    Label = Zh("秒表", "Stopwatch"),
                    CommandId = "open",
                    PageId = PageId,
                    Order = 110,
                },
            ],
            ToolPages =
            [
                new ToolPageContribution
                {
                    ContributionId = PageId,
                    Title = Zh("秒表", "Stopwatch"),
                    Order = 110,
                    Nodes = BuildNodes(),
                },
            ],
        });
    }

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        switch (commandId)
        {
            case "open":
                await PushToolPageAsync();
                return CommandInvokeOutcome.Complete();
            case "toggle-run":
                await ToggleRunAsync();
                return CommandInvokeOutcome.Complete();
            case "lap":
                return await RecordLapAsync();
            case "reset":
                return await ResetAsync();
            case "export-csv":
            case "export-xlsx":
                if (_activeTaskId is not null)
                {
                    return CommandInvokeOutcome.Reject(Zh("已有导出任务在进行", "An export task is already running"));
                }

                bool excel = commandId == "export-xlsx";
                StartTask(commandId, context => RunExportAsync(context, excel));
                return CommandInvokeOutcome.Accept(commandId);
            default:
                return CommandInvokeOutcome.Reject($"Unknown command '{commandId}'.");
        }
    }

    private async Task ToggleRunAsync()
    {
        long now = NowUnixMs();
        lock (_gate)
        {
            _session = _session.Mode switch
            {
                StopwatchMode.Idle => TimerEngine.Start(_session, now),
                StopwatchMode.Running => TimerEngine.Pause(_session, now),
                StopwatchMode.Paused => TimerEngine.Resume(_session, now),
                _ => _session,
            };
            _resetArmed = false;
        }

        await PersistAndPushAsync();
    }

    private async Task<CommandInvokeOutcome> RecordLapAsync()
    {
        long now = NowUnixMs();
        LapRecord? lap;
        lock (_gate)
        {
            (_session, lap) = TimerEngine.RecordLap(_session, now);
        }

        if (lap is null)
        {
            return CommandInvokeOutcome.Reject(Zh("计时未开始，无法计次", "The stopwatch is not running"));
        }

        await PersistAndPushAsync();
        return CommandInvokeOutcome.Complete();
    }

    private async Task<CommandInvokeOutcome> ResetAsync()
    {
        long now = NowUnixMs();
        bool armedNow;
        lock (_gate)
        {
            if (_session.Mode == StopwatchMode.Idle && _session.Laps.Count == 0)
            {
                return CommandInvokeOutcome.Complete(Zh("没有可重置的内容", "Nothing to reset"));
            }

            if (!_resetArmed)
            {
                _resetArmed = true;
                _resetArmedAtUnixMs = now;
                armedNow = true;
            }
            else
            {
                _previous = _session.Mode == StopwatchMode.Running ? TimerEngine.Pause(_session, now) : _session;
                if (_previous.Laps.Count == 0)
                {
                    _previous = null;
                }

                _session = new TimerSession();
                _resetArmed = false;
                _status = Zh("已重置，上次记录已保留", "Reset; the previous session was kept");
                armedNow = false;
            }
        }

        await PersistAndPushAsync();
        if (armedNow)
        {
            _ = DisarmResetLaterAsync(_resetArmedAtUnixMs);
        }

        return CommandInvokeOutcome.Complete();
    }

    private async Task DisarmResetLaterAsync(long armedAtUnixMs)
    {
        long deadline = armedAtUnixMs + (long)ResetArmWindow.TotalMilliseconds;
        long remaining = deadline - NowUnixMs();
        if (remaining > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remaining));
        }

        bool changed = false;
        lock (_gate)
        {
            if (_resetArmed && _resetArmedAtUnixMs == armedAtUnixMs)
            {
                _resetArmed = false;
                changed = true;
            }
        }

        if (changed)
        {
            await PushToolPageAsync();
        }
    }

    private async Task RunExportAsync(PluginTaskContext context, bool excel)
    {
        _activeTaskId = context.TaskId;
        OutputHandle? output = null;
        try
        {
            TimerSession target;
            lock (_gate)
            {
                target = _session.Laps.Count > 0 ? _session : _previous ?? _session;
            }

            if (target.Laps.Count == 0)
            {
                Status = Zh("没有可导出的计次记录", "No lap records to export");
                return;
            }

            bool chinese = IsChinese();
            string extension = excel ? "xlsx" : "csv";
            string suggestName = $"DuCom-{Zh("秒表", "stopwatch")}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}";
            FilesPickResult? picked;
            try
            {
                picked = await Api.Files.PickWriteAsync(FilePickWriteOptions.SaveFile(suggestName), context.Token);
            }
            catch (PluginHostException exception) when (string.Equals(exception.Code, PluginErrorCode.Cancelled, StringComparison.Ordinal))
            {
                return;
            }

            if (picked is null)
            {
                return;
            }

            byte[] data = excel
                ? MinimalXlsx.BuildLapWorkbook(target, DateTimeOffset.Now, chinese)
                : BuildCsv(target, DateTimeOffset.Now, chinese);
            output = await Api.Output.BeginAsync(context.Token);
            await Api.Output.WriteAsync(output.Token, 0, data, context.Token);
            string commitId = Guid.NewGuid().ToString("N");
            FilesCommitResult commit = await Api.Output.CommitAsync(output.Token, picked.Token, commitId, context.Token);
            output = null;
            string message = string.Format(Zh("已导出：{0}", "Exported: {0}"), commit.FinalPath);
            Status = message;
            Api.Diagnostics.Info($"Stopwatch lap export committed: {commit.Bytes} bytes");
            try
            {
                await Api.Ui.NotifyAsync(Zh("导出完成", "Export complete"), message, commit.FinalPath, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Api.Diagnostics.Warning($"Export notice failed: {exception.Message}");
            }
        }
        catch (Exception exception)
        {
            Status = $"{Zh("导出失败", "Export failed")}: {exception.Message}";
            Api.Diagnostics.LogError($"Stopwatch export raised an exception: {exception.Message}");
        }
        finally
        {
            if (output is not null)
            {
                try { await Api.Output.DiscardAsync(output.Token, CancellationToken.None); } catch (Exception) { }
            }

            _activeTaskId = null;
        }
    }

    private static byte[] BuildCsv(TimerSession session, DateTimeOffset exportedAt, bool chinese)
    {
        StringBuilder builder = new();
        builder.AppendLine(chinese ? "序号,单次时间,累计时间,记录时刻" : "Index,Lap,Total,Wall clock");
        foreach (LapRecord lap in session.Laps)
        {
            builder.Append(lap.Index).Append(',')
                .Append(FormatMs(lap.LapMs)).Append(',')
                .Append(FormatMs(lap.TotalMs)).Append(',')
                .AppendFormat("{0:yyyy-MM-dd HH:mm:ss.fff}", DateTimeOffset.FromUnixTimeMilliseconds(lap.WallClockUnixMs).ToLocalTime())
                .AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine(chinese ? $"导出时间,{exportedAt:yyyy-MM-dd HH:mm:ss}" : $"Exported,{exportedAt:yyyy-MM-dd HH:mm:ss}");
        // UTF-8 BOM so Excel opens Chinese content without garbling.
        return [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(builder.ToString())];
    }

    private static string FormatMs(long milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)).ToString(@"hh\:mm\:ss\.fff", System.Globalization.CultureInfo.InvariantCulture);

    private async Task PersistAndPushAsync()
    {
        try
        {
            TimerPluginState state;
            lock (_gate)
            {
                state = new TimerPluginState(_session, _previous);
            }

            await Api.Storage.WriteAsync(JsonSerializer.Serialize(state, TimerEngine.JsonOptions), CancellationToken.None);
        }
        catch (Exception exception) when (exception is PluginHostException or InvalidOperationException)
        {
            Api.Diagnostics.Warning($"Stopwatch state persistence failed: {exception.Message}");
        }

        await PushToolPageAsync();
    }

    private async Task PushToolPageAsync()
    {
        try
        {
            await Api.Ui.UpdateToolPageAsync(PageId, BuildNodes());
        }
        catch (PluginHostException)
        {
        }
    }

    private IReadOnlyList<UiNode> BuildNodes()
    {
        TimerSession session;
        TimerSession? previous;
        bool resetArmed;
        string status;
        lock (_gate)
        {
            session = _session;
            previous = _previous;
            resetArmed = _resetArmed;
            status = _status;
        }

        string state = session.Mode switch
        {
            StopwatchMode.Running => $"running:{session.AnchorUnixMs ?? NowUnixMs()}:{session.AccumulatedMs}",
            StopwatchMode.Paused => $"paused:{session.AccumulatedMs}",
            _ => "idle",
        };

        string statusText = session.Mode switch
        {
            StopwatchMode.Running => string.Format(Zh("运行中 · {0} 次计次", "Running · {0} laps"), session.Laps.Count),
            StopwatchMode.Paused => string.Format(Zh("已暂停 · {0} 次计次", "Paused · {0} laps"), session.Laps.Count),
            _ => previous is null
                ? Zh("点击「开始」启动秒表", "Press Start to begin")
                : string.Format(
                    Zh("上次记录 {0} · {1} 次计次", "Last session {0} · {1} laps"),
                    TimerEngine.FormatElapsed(previous.ElapsedMs(NowUnixMs())),
                    previous.Laps.Count),
        };

        string toggleLabel = session.Mode switch
        {
            StopwatchMode.Running => Zh("暂停", "Pause"),
            StopwatchMode.Paused => Zh("继续", "Resume"),
            _ => Zh("开始", "Start"),
        };

        List<UiListItem> lapItems = [];
        for (int index = session.Laps.Count - 1; index >= 0; index--)
        {
            LapRecord lap = session.Laps[index];
            lapItems.Add(new UiListItem
            {
                Text = lap.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Detail = FormattableString.Invariant($"{lap.LapMs}|{lap.TotalMs}|{lap.WallClockUnixMs}"),
            });
        }

        return
        [
            new UiPanelNode
            {
                Direction = UiDirection.Vertical,
                Children =
                [
                    new UiTextNode { FieldId = "stopwatchState", Text = state, ReadOnly = true },
                    new UiListNode { Id = "stopwatchLaps", Items = lapItems },
                    new UiTextNode { FieldId = "stopwatchStatus", Text = status.Length > 0 ? $"{statusText}\n{status}" : statusText, ReadOnly = true, Multiline = true },
                    new UiPanelNode
                    {
                        Direction = UiDirection.Horizontal,
                        Children =
                        [
                            new UiButtonNode { CommandId = "toggle-run", Text = toggleLabel, Accent = true },
                            new UiButtonNode { CommandId = "lap", Text = Zh("计次", "Lap") },
                            new UiButtonNode { CommandId = "reset", Text = resetArmed ? Zh("确认重置", "Confirm reset") : Zh("重置", "Reset") },
                            new UiButtonNode { CommandId = "export-csv", Text = Zh("导出 CSV", "Export CSV") },
                            new UiButtonNode { CommandId = "export-xlsx", Text = Zh("导出 Excel", "Export Excel") },
                        ],
                    },
                ],
            },
        ];
    }

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private bool IsChinese() => Api.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private string Zh(string chinese, string english) => IsChinese() ? chinese : english;
}
