using System.IO.Compression;
using System.Text;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.Plugins.LogPackage;

public sealed partial class Plugin
{
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
}
