using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.LogPackage;

public sealed class Plugin : DuComPlugin
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

    private Task PreFillReproductionTimeAsync()
    {
        lock (_gate)
        {
            // Legacy behavior: every time the packager opens, the reproduction time defaults
            // to that moment (the user can copy the live clock later or edit freely).
            _preferences.ReproductionTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        }

        return Task.CompletedTask;
    }

    private async Task BrowseOutputAsync(CancellationToken cancellationToken)
    {
        FilesPickResult? picked;
        try
        {
            // Directory mode rides on the write permission this plugin already holds; the
            // picked folder is remembered so pack-time target creation can resolve it.
            picked = await Api.Files.PickWriteAsync(FilePickWriteOptions.Directory(), cancellationToken);
        }
        catch (PluginHostException exception) when (string.Equals(exception.Code, PluginErrorCode.Cancelled, StringComparison.Ordinal))
        {
            return;
        }

        if (picked is null)
        {
            return;
        }

        lock (_gate)
        {
            _preferences.OutputDirectory = picked.DisplayPath;
            _preferences.FollowLogDirectory = false;
        }

        await PersistAsync(cancellationToken);
        await PushToolPageAsync();
    }

    private async Task ToggleFollowLogDirectoryAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        bool follow = values.TryGetValue("followLogDirectory", out string? value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        await SaveFormAsync(values.Where(pair => pair.Key != "followLogDirectory").ToDictionary(pair => pair.Key, pair => pair.Value), cancellationToken);
        lock (_gate)
        {
            _preferences.FollowLogDirectory = follow;
            if (follow)
            {
                _preferences.OutputDirectory = string.Empty;
            }
            else if (string.IsNullOrWhiteSpace(_preferences.OutputDirectory))
            {
                _preferences.OutputDirectory = _logDirectory;
            }
        }

        await PersistAsync(cancellationToken);
        await PushToolPageAsync();
    }

    private async Task SaveFormAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (values.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (values.TryGetValue("projectName", out string? projectName))
            {
                _preferences.ProjectName = projectName ?? string.Empty;
            }

            if (values.TryGetValue("title", out string? title))
            {
                _preferences.Title = title ?? string.Empty;
            }

            if (values.TryGetValue("tester", out string? tester))
            {
                _preferences.Tester = tester ?? string.Empty;
            }

            if (values.TryGetValue("deviceSoftwareVersion", out string? version))
            {
                _preferences.DeviceSoftwareVersion = version ?? string.Empty;
            }

            if (values.TryGetValue("reproductionProbability", out string? probability))
            {
                _preferences.ReproductionProbability = probability ?? string.Empty;
            }

            if (values.TryGetValue("reproductionTime", out string? reproductionTime))
            {
                _preferences.ReproductionTime = reproductionTime ?? string.Empty;
            }

            var selection = values.Where(pair => pair.Key.StartsWith("selection:", StringComparison.Ordinal)).ToArray();
            _preferences.SessionSelection = selection.Length == 0 ? null : selection.ToDictionary(
                pair => pair.Key[10..], pair => string.Equals(pair.Value, "true", StringComparison.OrdinalIgnoreCase), StringComparer.Ordinal);
            Dictionary<string, string> devices = new(_preferences.PortDevices, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values.Where(pair => pair.Key.StartsWith("device:", StringComparison.Ordinal)))
                devices[pair.Key[7..]] = pair.Value;
            _preferences.PortDevices = devices;

            if (values.TryGetValue("problemDescription", out string? description))
            {
                _preferences.ProblemDescription = description ?? string.Empty;
            }

            if (values.TryGetValue("reproductionSteps", out string? steps))
            {
                _preferences.ReproductionSteps = steps ?? string.Empty;
            }

            if (values.TryGetValue("notes", out string? notes))
            {
                _preferences.Notes = notes ?? string.Empty;
            }

            if (values.TryGetValue("portDevices", out string? portDevices))
            {
                _preferences.PortDevices = ParsePortDevices(portDevices);
            }
        }

        await PersistAsync(cancellationToken);
        await PushToolPageAsync();
    }

    private static Dictionary<string, string> ParsePortDevices(string? text)
    {
        Dictionary<string, string> devices = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
        {
            return devices;
        }

        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            devices[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return devices;
    }

    private async Task RunPackAsync(PluginTaskContext context)
    {
        _activeTaskId = context.TaskId;
        List<LogSnapshot> snapshots = [];
        OutputHandle? output = null;
        bool committed = false;
        string? commitId = null;
        try
        {
            if (!await ResolvePendingCommitAsync()) return;
            LogPackagePreferences preferences;
            lock (_gate)
            {
                preferences = _preferences with { };
            }

            IReadOnlyList<DuCom.Plugin.Dto.SerialSessionInfo> sessions = await Api.Logs.ListSessionsAsync(context.Token);
            if (sessions.Count == 0)
            {
                Status = Zh("没有打开的串口会话，无法打包", "No open serial sessions to package");
                return;
            }

            if (preferences.SessionSelection is { } selected)
                sessions = sessions.Where(session => selected.GetValueOrDefault(session.SessionId)).ToArray();
            if (sessions.Count == 0)
            {
                Status = Zh("请至少选择一个串口会话", "Select at least one serial session");
                return;
            }

            Status = Zh("正在创建日志快照…", "Creating log snapshot...");
            if (preferences.SessionSelection is null)
            {
                LogSnapshot? all = await Api.Logs.CreateSnapshotAsync(null, context.Token);
                if (all is not null) snapshots.Add(all);
            }
            else
            {
                foreach (var session in sessions)
                {
                    LogSnapshot? snapshot = await Api.Logs.CreateSnapshotAsync(session.SessionId, context.Token);
                    if (snapshot is not null)
                        snapshots.Add(snapshot with { Files = snapshot.Files.Where(file => string.Equals(file.Port, session.Port, StringComparison.OrdinalIgnoreCase)).ToArray() });
                }
            }
            if (snapshots.All(snapshot => snapshot.Files.Count == 0))
            {
                Status = Zh("日志快照为空", "The log snapshot is empty");
                return;
            }

            // Directory-based output like the built-in feature: default silently into the
            // application log directory (exe root\Logs), or into the user-chosen folder.
            string? directory = preferences.FollowLogDirectory || string.IsNullOrWhiteSpace(preferences.OutputDirectory) ? null : preferences.OutputDirectory;
            FilesPickResult? target = await CreateWriteTargetWithFallbackAsync(directory, context.Token);
            if (target is null)
            {
                Status = Zh("无法创建输出文件", "Cannot create the output file");
                return;
            }

            _targetToken = target.Token;
            _targetDisplay = target.DisplayPath;
            Status = string.Format(Zh("正在写入压缩包：{0}", "Writing archive: {0}"), target.DisplayPath);

            output = await Api.Output.BeginAsync(context.Token);
            long written = await WriteArchiveAsync(output, snapshots, sessions, preferences, context);
            context.ReportProgress(95, Zh("正在提交输出…", "Committing output..."), written);

            commitId = Guid.NewGuid().ToString("N");
            _pendingCommit = (commitId, output.Token);
            DuCom.Plugin.Dto.FilesCommitResult commit = await Api.Output.CommitAsync(output.Token, target.Token, commitId, context.Token);
            committed = true;
            _pendingCommit = null;
            _targetToken = null;
            string message = string.Format(
                Api.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "日志包已生成：{0}（{1:N0} 字节）" : "Log package created: {0} ({1:N0} bytes)",
                commit.FinalPath,
                commit.Bytes);
            Status = message;
            context.ReportProgress(100, null, commit.Bytes);
            Api.Diagnostics.Info($"Log package committed: {commit.Bytes} bytes");
            await NotifySuccessAsync(commit.FinalPath, message);
        }
        catch (Exception exception)
        {
            if (commitId is not null) await ResolvePendingCommitAsync();
            else Status = exception is OperationCanceledException
                ? Zh("已取消，输出未提交", "Cancelled before output commit")
                : $"{Zh("打包失败", "Package failed")}: {exception.Message}";
            Api.Diagnostics.LogError($"Package operation raised an exception: commit={commitId ?? "not-started"}: {exception.Message}");
        }
        finally
        {
            if (output is not null && !committed && commitId is null)
            {
                try { await Api.Output.DiscardAsync(output.Token, CancellationToken.None); } catch (Exception) { }
            }
            foreach (LogSnapshot snapshot in snapshots)
            {
                try { await Api.Logs.ReleaseSnapshotAsync(snapshot.Token, CancellationToken.None); } catch (Exception) { }
            }
            _activeTaskId = null;
            _progressPercent = null;
        }
    }

    private async Task<FilesPickResult?> CreateWriteTargetWithFallbackAsync(string? directory, CancellationToken cancellationToken)
    {
        string suggestName = SuggestZipName();
        try
        {
            return await Api.Files.CreateWriteTargetAsync(directory, suggestName, cancellationToken);
        }
        catch (PluginHostException exception) when (directory is not null && string.Equals(exception.Code, PluginErrorCode.PermissionDenied, StringComparison.Ordinal))
        {
            // The stored folder predates the grant store (or was revoked); fall back to the
            // host-managed log directory so packing still succeeds.
            Api.Diagnostics.Warning($"Output directory '{directory}' is no longer granted; falling back to the log directory.");
            lock (_gate)
            {
                _preferences.FollowLogDirectory = true;
                _preferences.OutputDirectory = string.Empty;
            }

            return await Api.Files.CreateWriteTargetAsync(null, suggestName, cancellationToken);
        }
    }

    private async Task NotifySuccessAsync(string finalPath, string message)
    {
        try
        {
            await Api.Ui.NotifyAsync(Zh("打包完成", "Package complete"), message, finalPath, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Api.Diagnostics.Warning($"Success notice failed: {exception.Message}");
        }
    }

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

    private async Task<long> WriteArchiveAsync(OutputHandle output, IReadOnlyList<LogSnapshot> snapshots, IReadOnlyList<DuCom.Plugin.Dto.SerialSessionInfo> sessions, LogPackagePreferences preferences, PluginTaskContext context)
    {
        long totalBytes = snapshots.Sum(snapshot => snapshot.TotalBytes);
        long processed = 0;
        await using HostOutputStream archiveStream = new(Api.Output, output, context.Token);
        using ZipArchive archive = new(archiveStream, ZipArchiveMode.Create, leaveOpen: true);

        ZipArchiveEntry description = archive.CreateEntry("问题详细描述.txt");
        await using (Stream descriptionStream = description.Open())
        {
            byte[] descriptionBytes = Encoding.UTF8.GetBytes(BuildDescription(preferences, sessions));
            await descriptionStream.WriteAsync(descriptionBytes, context.Token);
        }

        string logFolder = "日志/";
        foreach (LogSnapshot snapshot in snapshots)
        foreach (LogSnapshotFileEntry file in snapshot.Files)
        {
            context.Token.ThrowIfCancellationRequested();
            string deviceName = preferences.PortDevices.TryGetValue(file.Port, out string? device) && !string.IsNullOrWhiteSpace(device)
                ? device
                : file.Port;
            string entryName = $"{logFolder}{SanitizeZipSegment(Path.GetFileNameWithoutExtension(file.Name))}-{SanitizeZipSegment(deviceName)}{Path.GetExtension(file.Name)}";
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            await using Stream entryStream = entry.Open();
            long offset = 0;
            while (offset < file.Length)
            {
                int chunkSize = (int)Math.Min(Api.Limits.ReadChunkMaxBytes, file.Length - offset);
                DuCom.Plugin.FileReadChunk chunk = await Api.Logs.ReadChunkAsync(snapshot.Token, file.Index, offset, chunkSize, context.Token);
                if (chunk.Data.Length == 0)
                {
                    throw new InvalidDataException("The log snapshot ended before its declared boundary.");
                }

                await entryStream.WriteAsync(chunk.Data, context.Token);
                offset += chunk.Data.Length;
                processed += chunk.Data.Length;
                int percent = totalBytes > 0 ? (int)(processed * 90 / totalBytes) : 50;
                _progressPercent = percent;
                context.ReportProgress(percent, null, processed);
            }
        }

        archive.Dispose();
        await archiveStream.FlushAsync(context.Token);
        return archiveStream.Length;
    }

    private sealed class HostOutputStream(IPluginOutput output, OutputHandle handle, CancellationToken cancellationToken) : Stream
    {
        // Keep the base64 JSON payload below the large object heap threshold in both worker
        // and host while preserving bounded asynchronous backpressure.
        private readonly byte[] _buffer = new byte[Math.Min(handle.ChunkMaxBytes, 48 * 1024)];
        private int _buffered;
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length + _buffered;
        public override long Position { get => Length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int take = Math.Min(_buffer.Length - _buffered, data.Length);
                data[..take].CopyTo(_buffer.AsSpan(_buffered));
                _buffered += take;
                data = data[take..];
                if (_buffered == _buffer.Length) Flush();
            }
        }

        public override void Flush() => FlushAsync(cancellationToken).GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_buffered == 0) return;
            long next = await output.WriteAsync(handle.Token, _length, _buffer.AsMemory(0, _buffered), cancellationToken).ConfigureAwait(false);
            if (next != _length + _buffered) throw new InvalidDataException("Host output acknowledged an unexpected length.");
            _length = next;
            _buffered = 0;
        }

        public override async ValueTask DisposeAsync()
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string SanitizeZipSegment(string value)
    {
        string cleaned = new string(value.Where(character => character >= ' ' && character != '/' && character != '\\' && character != ':').ToArray()).Trim('.', ' ');
        return string.IsNullOrEmpty(cleaned) ? "unnamed" : cleaned[..Math.Min(cleaned.Length, 120)];
    }

    private static string BuildDescription(LogPackagePreferences preferences, IReadOnlyList<DuCom.Plugin.Dto.SerialSessionInfo> sessions)
    {
        StringBuilder builder = new();
        builder.AppendLine("项目名称: " + preferences.ProjectName);
        builder.AppendLine("问题标题: " + preferences.Title);
        builder.AppendLine("测试人员: " + preferences.Tester);
        builder.AppendLine("设备软件版本: " + preferences.DeviceSoftwareVersion);
        builder.AppendLine("复现概率: " + preferences.ReproductionProbability);
        builder.AppendLine("复现时间: " + (string.IsNullOrWhiteSpace(preferences.ReproductionTime) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : preferences.ReproductionTime));
        builder.AppendLine("打包时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();
        builder.AppendLine("串口 -> 设备映射:");
        foreach (DuCom.Plugin.Dto.SerialSessionInfo session in sessions)
        {
            string device = preferences.PortDevices.TryGetValue(session.Port, out string? mapped) && !string.IsNullOrWhiteSpace(mapped) ? mapped : session.Port;
            builder.AppendLine($"  {session.Port} -> {device}");
        }

        builder.AppendLine();
        builder.AppendLine("问题描述:");
        builder.AppendLine(preferences.ProblemDescription);
        builder.AppendLine();
        builder.AppendLine("复现步骤:");
        builder.AppendLine(preferences.ReproductionSteps);
        builder.AppendLine();
        builder.AppendLine("备注:");
        builder.AppendLine(preferences.Notes);
        return builder.ToString();
    }

    private string SuggestZipName()
    {
        string project = Sanitize(string.IsNullOrWhiteSpace(_preferences.ProjectName) ? "DuCom" : _preferences.ProjectName);
        string title = Sanitize(string.IsNullOrWhiteSpace(_preferences.Title) ? Zh("日志包", "logs") : _preferences.Title);
        string tester = Sanitize(string.IsNullOrWhiteSpace(_preferences.Tester) ? "NA" : _preferences.Tester);
        return $"{project}-{title}-{tester}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.zip";
    }

    private static string Sanitize(string value)
    {
        char[] invalid = [.. Path.GetInvalidFileNameChars()];
        string sanitized = string.Join("_", value.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return sanitized.Length == 0 ? "DuCom" : (sanitized.Length > 80 ? sanitized[..80] : sanitized);
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(_preferences, LogPackagePreferences.JsonOptions);
        await Api.Storage.WriteAsync(json, cancellationToken);
    }

    private async Task PushToolPageAsync()
    {
        try
        {
            await Api.Ui.UpdateToolPageAsync("packager", BuildFormNodes());
        }
        catch (PluginHostException)
        {
        }
    }

    private IReadOnlyList<UiNode> BuildFormNodes()
    {
        LogPackagePreferences preferences;
        string status;
        int? percent;
        lock (_gate)
        {
            preferences = _preferences with { };
            status = _status;
            percent = _progressPercent;
        }

        List<UiNode> rows =
        [
            new UiLabelNode { Text = Zh("日志打包", "Log package"), Style = UiTextStyle.Heading },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiTextNode { FieldId = "currentTime", ReadOnly = true, Placeholder = Zh("当前时间（毫秒）", "Current time (ms)") },
                ],
            },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiTextNode { FieldId = "projectName", Text = preferences.ProjectName, Placeholder = Zh("项目名称", "Project name") },
                    new UiTextNode { FieldId = "tester", Text = preferences.Tester, Placeholder = Zh("测试人员", "Tester") },
                ],
            },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiTextNode { FieldId = "title", Text = preferences.Title, Placeholder = Zh("问题标题", "Issue title") },
                    new UiTextNode { FieldId = "reproductionTime", Text = preferences.ReproductionTime, Placeholder = Zh("复现时间", "Reproduction time") },
                ],
            },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiTextNode { FieldId = "deviceSoftwareVersion", Text = preferences.DeviceSoftwareVersion, Placeholder = Zh("设备软件版本", "Device software version") },
                    new UiTextNode { FieldId = "reproductionProbability", Text = preferences.ReproductionProbability, Placeholder = Zh("复现概率", "Reproduction probability") },
                ],
            },
            new UiTextNode { FieldId = "outputDirectory", Text = EffectiveOutputDirectory(preferences), ReadOnly = true, Placeholder = Zh("输出目录", "Output directory") },
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiButtonNode { CommandId = "browse-output", Text = Zh("选择目录…", "Browse..."), SubmitForm = true },
                    new UiCheckBoxNode { FieldId = "followLogDirectory", Label = Zh("跟随日志目录", "Follow log directory"), IsChecked = preferences.FollowLogDirectory },
                ],
            },
            new UiTextNode { FieldId = "problemDescription", Text = preferences.ProblemDescription, Placeholder = Zh("问题描述", "Problem description"), Multiline = true },
            new UiTextNode { FieldId = "reproductionSteps", Text = preferences.ReproductionSteps, Placeholder = Zh("复现步骤", "Reproduction steps"), Multiline = true },
            new UiTextNode { FieldId = "notes", Text = preferences.Notes, Placeholder = Zh("备注", "Notes"), Multiline = true },
            new UiButtonNode { CommandId = "refresh-sessions", Text = Zh("刷新会话", "Refresh sessions"), SubmitForm = true },
            BuildSessionList(preferences),
            new UiPanelNode
            {
                Direction = UiDirection.Horizontal,
                Children =
                [
                    new UiButtonNode { CommandId = "save-form", Text = Zh("保存表单", "Save form"), SubmitForm = true },
                    new UiButtonNode { CommandId = "pack", Text = Zh("打包", "Package"), Accent = true, SubmitForm = true },
                    new UiButtonNode { CommandId = "cancel", Text = Zh("取消", "Cancel") },
                ],
            },
        ];

        if (percent.HasValue)
        {
            rows.Add(new UiProgressNode { Percent = percent, Label = status });
        }
        else if (status.Length > 0)
        {
            rows.Add(new UiLabelNode { Text = status, Style = UiTextStyle.Caption });
        }

        return [new UiPanelNode { Direction = UiDirection.Vertical, Children = rows }];
    }

    private string EffectiveOutputDirectory(LogPackagePreferences preferences)
    {
        if (!preferences.FollowLogDirectory && !string.IsNullOrWhiteSpace(preferences.OutputDirectory))
        {
            return preferences.OutputDirectory;
        }

        return string.IsNullOrEmpty(_logDirectory) ? Zh("（跟随日志目录）", "(follows the log directory)") : _logDirectory;
    }

    private UiNode BuildSessionList(LogPackagePreferences preferences)
    {
        try
        {
            IReadOnlyList<DuCom.Plugin.Dto.SerialSessionInfo> sessions = Api.Logs.ListSessionsAsync(CancellationToken.None).GetAwaiter().GetResult();
            List<UiNode> items = [];
            foreach (DuCom.Plugin.Dto.SerialSessionInfo session in sessions)
            {
                string device = preferences.PortDevices.TryGetValue(session.Port, out string? mapped) && !string.IsNullOrWhiteSpace(mapped) ? mapped : string.Empty;
                items.Add(new UiPanelNode { Direction = UiDirection.Horizontal, Children =
                [
                    new UiCheckBoxNode { FieldId = "selection:" + session.SessionId, Label = session.Port, IsChecked = preferences.SessionSelection is null || preferences.SessionSelection.GetValueOrDefault(session.SessionId) },
                    new UiTextNode { FieldId = "device:" + session.Port, Text = device, Placeholder = Zh("设备名", "Device name") },
                ] });
            }

            return new UiPanelNode { Direction = UiDirection.Vertical, Children = items };
        }
        catch (Exception)
        {
            return new UiLabelNode { Text = Zh("无法枚举会话", "Cannot enumerate sessions"), Style = UiTextStyle.Caption };
        }
    }

    public override Task<SettingsApplyOutcome> OnSettingsApplyAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => Task.FromResult(SettingsApplyOutcome.Success);

    private string Zh(string chinese, string english) => Api.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? chinese : english;
}
