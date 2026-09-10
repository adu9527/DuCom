using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.LogPackage;

public sealed partial class Plugin : DuComPlugin
{
    private readonly Lock _gate = new();
    private LogPackagePreferences _preferences = new();
    private string _status = string.Empty;
    private int? _progressPercent;
    private string? _targetToken;
    private string _targetDisplay = string.Empty;
    private string _logDirectory = string.Empty;
    private volatile string? _activeTaskId;
    private (string CommitId, string OutputToken)? _pendingCommit;

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
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                LogPackagePreferences? loaded = JsonSerializer.Deserialize<LogPackagePreferences>(json, LogPackagePreferences.JsonOptions);
                if (loaded is not null)
                {
                    _preferences = loaded;
                }
            }
            catch (JsonException)
            {
            }
        }

        await TryRefreshLogDirectoryAsync();
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
                    ContributionId = "open-packager",
                    Label = Zh("日志打包", "Log package"),
                    CommandId = "open",
                    PageId = "packager",
                    Order = 100,
                },
            ],
            ToolPages =
            [
                new ToolPageContribution
                {
                    ContributionId = "packager",
                    Title = Zh("日志打包", "Log package"),
                    Order = 100,
                    Nodes = BuildFormNodes(),
                },
            ],
        });
    }

    public override Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _targetToken = null;
        return Task.CompletedTask;
    }

    public override async Task<CommandInvokeOutcome> OnCommandAsync(string commandId, string? arg, IReadOnlyDictionary<string, string> formValues, CancellationToken cancellationToken)
    {
        switch (commandId)
        {
            case "open":
                await TryRefreshLogDirectoryAsync();
                await PreFillReproductionTimeAsync();
                await PushToolPageAsync();
                return CommandInvokeOutcome.Complete();
            case "save-form":
                await SaveFormAsync(formValues, cancellationToken);
                return CommandInvokeOutcome.Complete(Zh("已保存", "Saved"));
            case "browse-output":
                await SaveFormAsync(formValues, cancellationToken);
                await BrowseOutputAsync(cancellationToken);
                return CommandInvokeOutcome.Complete();
            case "toggle-follow":
                await ToggleFollowLogDirectoryAsync(formValues, cancellationToken);
                return CommandInvokeOutcome.Complete();
            case "refresh-sessions":
                await SaveFormAsync(formValues, cancellationToken);
                if (_activeTaskId is null) await ResolvePendingCommitAsync();
                await TryRefreshLogDirectoryAsync();
                await PushToolPageAsync();
                return CommandInvokeOutcome.Complete();
            case "pack":
                if (_activeTaskId is not null)
                {
                    return CommandInvokeOutcome.Reject(Zh("已有打包任务在进行", "A package task is already running"));
                }

                await SaveFormAsync(formValues, cancellationToken);
                StartTask("pack", context => RunPackAsync(context));
                return CommandInvokeOutcome.Accept("pack");
            case "cancel":
                string? taskId = _activeTaskId;
                if (taskId is null)
                {
                    return CommandInvokeOutcome.Complete(Zh("没有进行中的任务", "No active task"));
                }

                Status = Zh("正在取消…", "Cancelling...");
                return CancelTask(taskId)
                    ? CommandInvokeOutcome.Complete()
                    : CommandInvokeOutcome.Complete(Zh("任务已结束", "The task has already ended"));
            default:
                return CommandInvokeOutcome.Reject($"Unknown command '{commandId}'.");
        }
    }

    private async Task TryRefreshLogDirectoryAsync()
    {
        try
        {
            DuCom.Plugin.Dto.HostPathsResult paths = await Api.Files.GetHostPathsAsync(CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(paths.LogDirectory))
            {
                _logDirectory = paths.LogDirectory;
            }
        }
        catch (PluginHostException)
        {
        }
    }

    public override Task<SettingsApplyOutcome> OnSettingsApplyAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => Task.FromResult(SettingsApplyOutcome.Success);

    private string Zh(string chinese, string english) => Api.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
}
