using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DuCom.Plugin;
using DuCom.Plugin.Dto;

namespace DuCom.PluginHost.Core;

public sealed partial class PluginBroker
{
    private async Task<FilesSnapshotResult> SnapshotFilesAsync(FilesSnapshotRequest request, CancellationToken cancellationToken)
    {
        RequirePermission(Permission.FilesUserSelectedRead);
        if (request.TaskId.Length is < 8 or > 80 || request.TaskId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Task id is invalid.");
        if (request.Tokens.Count is < 1 or > 16 || request.Tokens.Distinct(StringComparer.Ordinal).Count() != request.Tokens.Count)
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Snapshot tokens are empty, duplicated, or exceed 16 files.");
        string root = Path.Combine(_scope.HostSnapshotDirectory, "tasks", request.TaskId);
        if (Directory.Exists(root)) throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Task snapshot id was already used.");
        Directory.CreateDirectory(root);
        List<FileSnapshotEntry> files = [];
        long totalLength = 0;
        string resourceId = _scope.RegisterResourceIntent(HostTempResourceKind.SnapshotDirectory, root);
        try
        {
            for (int index = 0; index < request.Tokens.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string token = request.Tokens[index];
                FileGrant grant = ResolveGrant(token, write: false);
                if (grant.IsDirectory || !File.Exists(grant.Path)) throw new PluginScopeException(PluginErrorCode.NotFound, "A snapshotted input file is missing.");
                string destination = Path.Combine(root, index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(grant.Path));
                using FileStream source = new(grant.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using FileStream target = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using System.Security.Cryptography.IncrementalHash hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                byte[] buffer = new byte[128 * 1024];
                int read;
                long length = 0;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (totalLength > _scope.Limits.TempQuotaBytes - read) throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Task input snapshots exceed the activation quota.");
                    if (!_scope.TryReserveResource(resourceId, read)) throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Host task-snapshot storage quota is exhausted.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    length = checked(length + read);
                    totalLength = checked(totalLength + read);
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                string snapshotToken = _scope.CreateFileGrant(destination, isDirectory: false, write: false);
                files.Add(new FileSnapshotEntry { Token = snapshotToken, Path = destination, Name = Path.GetFileName(grant.Path), Length = length, Sha256 = Convert.ToHexString(hash.GetHashAndReset()) });
            }
            return new FilesSnapshotResult { Files = files };
        }
        catch
        {
            _scope.CleanupResource(resourceId, HostTempResourceKind.SnapshotDirectory, root, totalLength);
            throw;
        }
    }

    private static FileStream CreateSecureStaging(string path)
    {
        const uint GenericRead = 0x80000000;
        const uint GenericWrite = 0x40000000;
        const uint Delete = 0x00010000;
        const uint CreateNew = 1;
        const uint SequentialScan = 0x08000000;
        const uint WriteThrough = 0x80000000;
        const uint Overlapped = 0x40000000;
        const uint OpenReparsePoint = 0x00200000;
        SafeFileHandle handle = CreateFileW(path, GenericRead | GenericWrite | Delete, 0, IntPtr.Zero, CreateNew, SequentialScan | WriteThrough | Overlapped | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException("Cannot create secure publication staging file.", new System.ComponentModel.Win32Exception(error));
        }
        return new FileStream(handle, FileAccess.ReadWrite, 64 * 1024, isAsync: true);
    }

    private static void RenameByHandle(SafeFileHandle handle, string destination, bool replaceApproved)
    {
        const int FileRenameInformation = 10;
        string nativeDestination = @"\??\" + Path.GetFullPath(destination);
        byte[] name = System.Text.Encoding.Unicode.GetBytes(nativeDestination);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        byte[] native = new byte[nameOffset + name.Length];
        IntPtr buffer = Marshal.AllocHGlobal(native.Length);
        try
        {
            Marshal.Copy(native, 0, buffer, native.Length);
            Marshal.WriteByte(buffer, replaceApproved ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, rootOffset, IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            int status = NtSetInformationFile(handle, out _, buffer, (uint)(nameOffset + name.Length), FileRenameInformation);
            if (status < 0)
            {
                int error = unchecked((int)RtlNtStatusToDosError(status));
                if (!replaceApproved && error is 80 or 183)
                    throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The selected target now exists and replacement was not approved.");
                throw new IOException("Secure publication rename failed.", new System.ComponentModel.Win32Exception(error));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);

    private static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new PluginScopeException(PluginErrorCode.PermissionDenied, "Reparse points are not allowed in output paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class DirectoryLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];

        public DirectoryLease(string path)
        {
            try
            {
                // Fail closed on platforms where directory replacement cannot be pinned here.
                if (!OperatingSystem.IsWindows())
                    throw new PluginScopeException(PluginErrorCode.UnsupportedOperation, "Secure output requires Windows directory handles.");
                Stack<string> parents = new();
                for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
                    parents.Push(current);
                foreach (string current in parents)
                {
                    SafeFileHandle handle = CreateFile(current, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        handle.Dispose();
                        throw new PluginScopeException(PluginErrorCode.PermissionDenied, "Cannot secure the output directory.");
                    }
                    _handles.Add(handle);
                    RejectReparsePoints(current);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (SafeFileHandle handle in _handles) handle.Dispose();
            _handles.Clear();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }

    private object UpdateUi(UiUpdateRequest request)
    {
        if (!_scope.HasCapability(Capability.ToolPage))
        {
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The manifest does not declare tool-page.");
        }

        if (!UiContributionValidator.Validate(request.Nodes, out string? error))
        {
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, error ?? "Invalid UI update.");
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_uiWindow)
        {
            while (_uiWindow.Count > 0 && _uiWindow.Peek() <= now - 60_000) _uiWindow.Dequeue();
            if (_uiWindow.Count >= _scope.Limits.UiUpdatesPerMinute)
                throw new PluginScopeException(PluginErrorCode.ResourceLimit, "UI update rate limit exceeded.");
            _uiWindow.Enqueue(now);
        }
        _environment.UpdateToolPage(_scope.Manifest.Id, request.ContributionId, request.Nodes);
        return new { ok = true };
    }

    private object BackgroundSet(BackgroundSetRequest request)
    {
        if (!_scope.HasCapability(Capability.BackgroundImage))
        {
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The manifest does not declare background-image.");
        }

        string? path = null;
        if (request.Token is { Length: > 0 })
        {
            FileGrant grant = _scope.ResolveToken(request.Token, write: false)
                ?? throw new PluginScopeException(PluginErrorCode.SessionExpired, "The image token is invalid or expired.");
            path = grant.Path;
        }

        double? opacity = request.Opacity is null ? null : Math.Clamp(request.Opacity.Value, 0d, 1d);
        _environment.ApplyBackground(new BackgroundApply { PluginId = _scope.Manifest.Id, ImageTokenPath = path, Opacity = opacity });
        return new { ok = true };
    }

    private object BackgroundClear()
    {
        if (!_scope.HasCapability(Capability.BackgroundImage))
        {
            throw new PluginScopeException(PluginErrorCode.PermissionDenied, "The manifest does not declare background-image.");
        }

        _environment.ApplyBackground(new BackgroundApply { PluginId = _scope.Manifest.Id, ImageTokenPath = null, Opacity = null });
        return new { ok = true };
    }

    private FileGrant ResolveGrant(string token, bool write)
    {
        return _scope.ResolveToken(token, write)
            ?? throw new PluginScopeException(PluginErrorCode.SessionExpired, "The file token is invalid or expired.");
    }

    private const string PermissionDeniedCode = PluginErrorCode.PermissionDenied;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool TryDeleteConfirmed(string path)
    {
        TryDelete(path);
        return !File.Exists(path);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                RejectReparsePoints(path);
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception)
        {
        }
    }
}
