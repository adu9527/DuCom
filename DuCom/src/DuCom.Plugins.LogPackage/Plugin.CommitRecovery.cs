using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.LogPackage;

public sealed partial class Plugin
{
    private async Task<bool> ResolvePendingCommitAsync()
    {
        if (_pendingCommit is not { } pending) return true;
        OutputCommitStatus? status = await QueryCommitStatusAsync(pending.CommitId);
        if (status?.IsCommitted == true)
        {
            _pendingCommit = null;
            _targetToken = null;
            Status = string.Format(Zh("提交已完成：{0}", "Commit completed: {0}"), status.FinalPath);
            return true;
        }
        if (status is { State: "aborted" })
        {
            try
            {
                await Api.Output.DiscardAsync(pending.OutputToken, CancellationToken.None);
                _pendingCommit = null;
                Status = Zh("提交已中止，输出已释放", "Commit aborted; output released");
                return true;
            }
            catch (Exception exception)
            {
                Api.Diagnostics.Warning($"Aborted output cleanup pending: commit={pending.CommitId}, output={pending.OutputToken}: {exception.Message}");
            }
        }
        Status = Zh("提交结果或清理尚未确认，请刷新重试", "Commit result or cleanup is unconfirmed; refresh to retry");
        Api.Diagnostics.Warning($"Commit recovery pending: commit={pending.CommitId}, output={pending.OutputToken}, state={status?.State ?? "query-failed"}");
        return false;
    }

    private async Task<OutputCommitStatus?> QueryCommitStatusAsync(string commitId)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            return await Api.Output.GetCommitStatusAsync(commitId, timeout.Token);
        }
        catch { return null; }
    }
}
