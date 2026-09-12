using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DuCom.Plugin;
using DuCom.Plugin.Dto;
using DuCom.PluginHost.Diagnostics;
using DuCom.PluginHost.Security;

namespace DuCom.PluginHost.Core;

internal sealed class NativeHelperTaskManager : IDisposable
{
    private const int MaximumPayloadBytes = 1024 * 1024;
    private const int CancelGraceMs = 3000;
    private readonly PluginManifest _manifest;
    private readonly string _packageDirectory;
    private readonly string _taskRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, RunningHelperTask> _tasks = new(StringComparer.Ordinal);
    private bool _disposed;

    public bool AllExitsConfirmed
    {
        get
        {
            lock (_gate) return _tasks.Values.All(task => task.ExitConfirmed);
        }
    }

    public NativeHelperTaskManager(PluginManifest manifest, string packageDirectory, string taskRoot)
    {
        _manifest = manifest;
        _packageDirectory = Path.GetFullPath(packageDirectory);
        _taskRoot = Path.GetFullPath(taskRoot);
        Directory.CreateDirectory(_taskRoot);
    }

    public HelperTaskResult Start(HelperStartRequest request)
    {
        ValidateTaskId(request.TaskId);
        if (Encoding.UTF8.GetByteCount(request.Payload) > MaximumPayloadBytes)
            throw new PluginScopeException(PluginErrorCode.ResourceLimit, "Helper task payload exceeds 1 MiB.");
        if (request.TimeoutMs is < 1000 or > 60 * 60 * 1000)
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Helper timeout must be between 1 second and 1 hour.");

        PluginNativeHelper helper = _manifest.NativeHelpers.FirstOrDefault(candidate => candidate.Id == request.HelperId)
            ?? throw new PluginScopeException(PluginErrorCode.NotFound, $"Native helper '{request.HelperId}' is not registered.");
        if (helper.ProtocolVersion != "1.0") throw new PluginScopeException(PluginErrorCode.UnsupportedOperation, "Native helper protocol is not supported.");
        string executable = ResolvePackagePath(helper.EntryPoint);
        string taskDirectory = Path.Combine(_taskRoot, request.TaskId);
        string requestPath = Path.Combine(taskDirectory, "request.json");
        string resultPath = Path.Combine(taskDirectory, "result.json");
        string cancelPath = Path.Combine(taskDirectory, "cancel.requested");
        string progressPath = Path.Combine(taskDirectory, "progress.json");
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_tasks.Count >= 32) throw new PluginScopeException(PluginErrorCode.ResourceLimit, "This activation has reached the 32 helper-task limit.");
            if (_tasks.ContainsKey(request.TaskId))
            {
                throw new PluginScopeException(PluginErrorCode.InvalidArgument, $"Task id '{request.TaskId}' was already used in this activation.");
            }

            // Reserve the identifier before process creation so concurrent duplicate starts can
            // never launch a second helper, even briefly.
            _tasks.Add(request.TaskId, RunningHelperTask.Reserved(request.TaskId));
        }

        RunningHelperTask task;
        try
        {
            Directory.CreateDirectory(taskDirectory);
            File.WriteAllText(requestPath, request.Payload, new UTF8Encoding(false));
            task = Launch(request.TaskId, executable, requestPath, resultPath, cancelPath, progressPath, request.TimeoutMs);
        }
        catch
        {
            lock (_gate) _tasks.Remove(request.TaskId);
            try { Directory.Delete(taskDirectory, true); }
            catch (Exception cleanupException)
            {
                PluginHostTrace.Warning($"Helper task directory cleanup failed for '{request.TaskId}'.", cleanupException);
            }

            throw;
        }
        lock (_gate)
        {
            if (_disposed)
            {
                task.KillAndConfirm(5000);
                task.DisposeHandles();
                throw new PluginScopeException(PluginErrorCode.SessionExpired, "Plugin activation ended while the helper was starting.");
            }
            _tasks[request.TaskId] = task;
        }
        _ = ObserveAsync(task);
        return task.Snapshot();
    }

    public HelperTaskResult Status(string taskId)
    {
        lock (_gate)
        {
            return Find(taskId).Snapshot();
        }
    }

    public async Task<HelperTaskResult> CancelAsync(string taskId)
    {
        RunningHelperTask task;
        lock (_gate) task = Find(taskId);
        if (task.IsTerminal) return task.Snapshot();
        try { File.WriteAllText(task.CancelPath, DateTimeOffset.UtcNow.ToString("O")); }
        catch (Exception exception)
        {
            // The helper will not observe the cooperative cancel marker; the forced
            // termination path below remains the backstop, so record and continue.
            PluginHostTrace.Warning($"Cancel marker could not be written for helper task '{taskId}'.", exception);
        }
        task.MarkCancelling();
        if (!await task.WaitForExitAsync(CancelGraceMs).ConfigureAwait(false))
        {
            bool confirmed = task.KillAndConfirm(5000);
            task.Complete("interrupted", null, null, confirmed ? "Helper required forced termination." : "Helper termination could not be confirmed.", confirmed);
        }
        else
        {
            try { await task.Terminal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        return task.Snapshot();
    }

    private static async Task ObserveAsync(RunningHelperTask task)
    {
        bool exited = await task.WaitForExitAsync(task.TimeoutMs).ConfigureAwait(false);
        if (!exited)
        {
            bool confirmed = task.KillAndConfirm(5000);
            task.Complete("interrupted", null, null, confirmed ? "Helper exceeded its task timeout and was terminated." : "Helper timed out and termination could not be confirmed.", confirmed);
            if (confirmed) task.DisposeHandles();
            return;
        }

        int exitCode = task.ExitCode;
        task.RefreshProgressEvidence(strict: true);
        if (task.ProgressError is { } progressError)
        {
            task.Complete("failed", exitCode, null, progressError, true);
            task.DisposeHandles();
            return;
        }
        HelperResultDocument? document = null;
        try
        {
            if (File.Exists(task.ResultPath))
            {
                FileInfo info = new(task.ResultPath);
                if (info.Length is > 0 and <= MaximumPayloadBytes)
                    document = JsonSerializer.Deserialize<HelperResultDocument>(File.ReadAllText(task.ResultPath), DtoJson.Options);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            task.Complete("failed", exitCode, null, $"Helper result is invalid: {exception.Message}", true);
            task.DisposeHandles();
            return;
        }

        if (document is null)
        {
            task.Complete("failed", exitCode, null, "Helper exited without a valid result document.", true);
            task.DisposeHandles();
            return;
        }
        if (document.State is not ("succeeded" or "failed" or "cancelled"))
        {
            task.Complete("failed", exitCode, null, "Helper reported an unsupported terminal state.", true);
            task.DisposeHandles();
            return;
        }
        if ((document.State == "succeeded") != (exitCode == 0))
        {
            task.Complete("failed", exitCode, null, "Helper result contradicts the process exit code.", true);
            task.DisposeHandles();
            return;
        }
        task.Complete(document.State, exitCode, document.Result, document.Error, true);
        task.DisposeHandles();
        task.CleanupFiles();
    }

    private static RunningHelperTask Launch(string taskId, string executable, string requestPath, string resultPath, string cancelPath, string progressPath, int timeoutMs)
    {
        IntPtr job = WindowsInterop.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new InvalidOperationException($"Helper Job creation failed ({Marshal.GetLastWin32Error()}).");
        WindowsInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new()
        {
            BasicLimitInformation = new WindowsInterop.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = WindowsInterop.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | WindowsInterop.JOB_OBJECT_LIMIT_ACTIVE_PROCESS | WindowsInterop.JOB_OBJECT_LIMIT_PROCESS_MEMORY,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = (UIntPtr)(512UL * 1024 * 1024),
        };
        if (!WindowsInterop.SetInformationJobObject(job, WindowsInterop.JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<WindowsInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            WindowsInterop.CloseHandle(job);
            throw new InvalidOperationException($"Helper Job configuration failed ({Marshal.GetLastWin32Error()}).");
        }

        string commandLine = $"{Quote(executable)} --ducom-task {Quote(requestPath)} --ducom-result {Quote(resultPath)} --ducom-cancel {Quote(cancelPath)} --ducom-progress {Quote(progressPath)}";
        WindowsInterop.STARTUPINFOW startup = new() { cb = (uint)Marshal.SizeOf<WindowsInterop.STARTUPINFOW>() };
        IntPtr startupPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsInterop.STARTUPINFOW>());
        Marshal.StructureToPtr(startup, startupPtr, false);
        try
        {
            if (!WindowsInterop.CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                WindowsInterop.CREATE_SUSPENDED | WindowsInterop.CREATE_UNICODE_ENVIRONMENT | WindowsInterop.CREATE_NO_WINDOW,
                IntPtr.Zero, Path.GetDirectoryName(executable), startupPtr, out WindowsInterop.PROCESS_INFORMATION process))
                throw new InvalidOperationException($"Helper process creation failed ({Marshal.GetLastWin32Error()}).");
            if (!WindowsInterop.AssignProcessToJobObject(job, process.hProcess))
            {
                WindowsInterop.TerminateJobObject(job, 1);
                WindowsInterop.CloseHandle(process.hProcess);
                WindowsInterop.CloseHandle(process.hThread);
                throw new InvalidOperationException($"Helper Job assignment failed ({Marshal.GetLastWin32Error()}).");
            }
            WindowsInterop.ResumeThread(process.hThread);
            WindowsInterop.CloseHandle(process.hThread);
            return new RunningHelperTask(taskId, process.hProcess, job, process.dwProcessId, resultPath, cancelPath, progressPath, timeoutMs);
        }
        catch
        {
            WindowsInterop.CloseHandle(job);
            throw;
        }
        finally { Marshal.FreeHGlobal(startupPtr); }
    }

    private string ResolvePackagePath(string relative)
    {
        string root = _packageDirectory + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(_packageDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new PluginScopeException(PluginErrorCode.NotFound, "Registered helper entry point is missing or outside the package.");
        return path;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static void ValidateTaskId(string taskId)
    {
        if (taskId.Length is < 8 or > 80 || taskId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new PluginScopeException(PluginErrorCode.InvalidArgument, "Task id must be 8-80 ASCII letters, digits, '-' or '_'.");
    }
    private RunningHelperTask Find(string id) => _tasks.TryGetValue(id, out RunningHelperTask? task) ? task : throw new PluginScopeException(PluginErrorCode.NotFound, $"Helper task '{id}' was not found.");
    private void ThrowIfDisposed() { if (_disposed) throw new PluginScopeException(PluginErrorCode.SessionExpired, "Helper task manager is disposed."); }
    public void Dispose()
    {
        RunningHelperTask[] tasks;
        lock (_gate) { if (_disposed) return; _disposed = true; tasks = [.. _tasks.Values]; }
        foreach (RunningHelperTask task in tasks.Where(task => !task.IsTerminal))
        {
            bool confirmed = task.KillAndConfirm(5000);
            task.Complete("interrupted", null, null, confirmed ? "Plugin activation ended before helper completion." : "Plugin activation ended and helper exit was not confirmed.", confirmed);
        }
        foreach (RunningHelperTask task in tasks) task.DisposeHandles();
    }

    private sealed record HelperResultDocument(string State, string? Result, string? Error);

    private sealed class RunningHelperTask
    {
        private readonly object _gate = new();
        private string _state = "running";
        private DateTimeOffset? _ended;
        private int? _exitCode;
        private string? _result;
        private string? _error;
        private int? _percent;
        private string? _message;
        private string? _progressError;
        private int _handlesDisposed;
        private bool _exitConfirmed;
        private readonly TaskCompletionSource _terminal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RunningHelperTask(string id, IntPtr process, IntPtr job, uint pid, string resultPath, string cancelPath, string progressPath, int timeoutMs)
        { Id = id; Process = process; Job = job; ProcessId = pid; ResultPath = resultPath; CancelPath = cancelPath; ProgressPath = progressPath; TimeoutMs = timeoutMs; Started = DateTimeOffset.UtcNow; }
        public static RunningHelperTask Reserved(string id) => new(id, IntPtr.Zero, IntPtr.Zero, 0, string.Empty, string.Empty, string.Empty, 0) { _state = "starting" };
        public string Id { get; } public IntPtr Process { get; } public IntPtr Job { get; } public uint ProcessId { get; }
        public string ResultPath { get; } public string CancelPath { get; } public string ProgressPath { get; } public int TimeoutMs { get; } public DateTimeOffset Started { get; }
        public bool IsTerminal { get { lock (_gate) return _ended.HasValue; } }
        public bool ExitConfirmed { get { lock (_gate) return _ended.HasValue && _exitConfirmed; } }
        public string? ProgressError { get { lock (_gate) return _progressError; } }
        public Task Terminal => _terminal.Task;
        public int ExitCode { get { WindowsInterop.GetExitCodeProcess(Process, out uint code); return unchecked((int)code); } }
        public void MarkCancelling() { lock (_gate) if (!_ended.HasValue) _state = "cancelling"; }
        public async Task<bool> WaitForExitAsync(int milliseconds) => await Task.Run(() => WindowsInterop.WaitForSingleObject(Process, milliseconds) == 0).ConfigureAwait(false);
        public bool WaitForExit(int milliseconds) => Process != IntPtr.Zero && WindowsInterop.WaitForSingleObject(Process, milliseconds) == 0;
        public bool KillAndConfirm(int milliseconds) => Job != IntPtr.Zero && WindowsInterop.TerminateJobObject(Job, 1) && WaitForExit(milliseconds);
        public void Kill() { _ = KillAndConfirm(0); }
        public void Complete(string state, int? exitCode, string? result, string? error, bool exitConfirmed = true)
        { lock (_gate) { if (_ended.HasValue) return; _state = state; _exitCode = exitCode; _result = result; _error = error; _exitConfirmed = exitConfirmed; _ended = DateTimeOffset.UtcNow; } _terminal.TrySetResult(); }
        public HelperTaskResult Snapshot()
        {
            RefreshProgressEvidence(strict: false);
            lock (_gate) return new HelperTaskResult { TaskId = Id, State = _state, StartedUtc = Started, EndedUtc = _ended, ExitCode = _exitCode, Result = _result, Error = _error, ExitConfirmed = _exitConfirmed, Percent = _percent, Message = _message };
        }
        public void RefreshProgressEvidence(bool strict)
        {
            if (string.IsNullOrEmpty(ProgressPath) || !File.Exists(ProgressPath)) return;
            try
            {
                ProgressDocument? progress = JsonSerializer.Deserialize<ProgressDocument>(File.ReadAllText(ProgressPath), DtoJson.Options);
                if (progress is null || progress.Percent is < 0 or > 100)
                    throw new JsonException("Progress document is empty or contains an out-of-range percentage.");
                lock (_gate) { _percent = progress.Percent; _message = progress.Message; }
            }
            catch (IOException exception)
            {
                if (strict) lock (_gate) _progressError ??= $"Helper progress could not be read after process exit: {exception.Message}";
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or JsonException)
            {
                lock (_gate) _progressError ??= $"Helper progress is invalid: {exception.Message}";
            }
        }
        public void CleanupFiles()
        {
            try
            {
                string? directory = Path.GetDirectoryName(ResultPath);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception exception)
            {
                PluginHostTrace.Warning($"Helper task result cleanup failed for '{Id}' at '{ResultPath}'.", exception);
            }
        }
        public void DisposeHandles() { if (Interlocked.Exchange(ref _handlesDisposed, 1) != 0) return; if (Process != IntPtr.Zero) WindowsInterop.CloseHandle(Process); if (Job != IntPtr.Zero) WindowsInterop.CloseHandle(Job); }
    }
    private sealed record ProgressDocument(int Percent, string? Message);
}
