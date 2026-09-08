using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DuCom.Plugin;

namespace DuCom.PluginHost.Security;

public sealed record WorkerStartupConfig
{
    public required string PluginId { get; init; }
    public required string PluginVersion { get; init; }
    public required string PackageDigest { get; init; }
    public required string PluginDirectory { get; init; }
    public required string EntryAssembly { get; init; }
    public required string EntryType { get; init; }
    public required string HostVersion { get; init; }
    public required string Culture { get; init; }
    public required IReadOnlyList<string> Capabilities { get; init; }
    public required IReadOnlyList<string> Permissions { get; init; }
    public required PluginLimits Limits { get; init; }
}

public sealed record WorkerSpawnResult
{
    public required IntPtr ProcessHandle { get; init; }
    public required IntPtr JobHandle { get; init; }
    public required uint ProcessId { get; init; }
    public required string PipeName { get; init; }
    public required string Credential { get; init; }
    public required string SessionId { get; init; }
    public required string ActivationId { get; init; }
    public required string AppContainerSid { get; init; }
}

/// <summary>
/// Spawns plugin worker processes: the same executable as the host (self-hosted runner),
/// inside a per-plugin AppContainer with no capabilities (network, devices, user files and
/// the host process stay out of reach; HKCU reads partially remain and are virtualized on
/// write), assigned to a Job Object while still suspended (no escape window), with the
/// one-time startup secret handed over an inherited anonymous stdin pipe instead of the
/// command line or logs.
/// </summary>
public sealed class SandboxLauncher : IDisposable
{
    private readonly string _hostExecutablePath;
    private readonly List<IntPtr> _pinnedMemory = [];

    public SandboxLauncher(string hostExecutablePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(hostExecutablePath);
        if (!File.Exists(hostExecutablePath))
        {
            throw new FileNotFoundException("Host executable not found.", hostExecutablePath);
        }

        _hostExecutablePath = hostExecutablePath;
    }

    public static string AppContainerMonikerForPlugin(string pluginId) => $"DuCom.Plugin.{pluginId}";

    public static SecurityIdentifier EnsureAppContainerProfile(string pluginId)
    {
        string moniker = AppContainerMonikerForPlugin(pluginId);
        int hr = WindowsInterop.CreateAppContainerProfile(moniker, moniker, "DuCom plugin worker sandbox", IntPtr.Zero, 0, out IntPtr sid);
        if (hr == unchecked((int)0x800700B7))
        {
            hr = WindowsInterop.DeriveAppContainerSidFromAppContainerName(moniker, out sid);
        }

        if (hr != 0)
        {
            throw new InvalidOperationException($"Cannot establish the AppContainer sandbox for '{pluginId}' (hr=0x{hr:X8}); refusing to run the plugin unrestricted.");
        }

        try
        {
            return new SecurityIdentifier(sid);
        }
        finally
        {
            Marshal.FreeHGlobal(sid);
        }
    }

    public WorkerSpawnResult Spawn(
        WorkerStartupConfig config,
        string storageDirectory,
        string tempDirectory,
        string hostOutputDirectory,
        string hostSnapshotDirectory,
        long processMemoryLimitBytes,
        string pipeName,
        string credential,
        string sessionId,
        string activationId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentException.ThrowIfNullOrEmpty(credential);
        ArgumentException.ThrowIfNullOrEmpty(storageDirectory);
        ArgumentException.ThrowIfNullOrEmpty(tempDirectory);
        ArgumentException.ThrowIfNullOrEmpty(hostOutputDirectory);
        ArgumentException.ThrowIfNullOrEmpty(hostSnapshotDirectory);

        SecurityIdentifier appContainerSid = EnsureAppContainerProfile(config.PluginId);

        WindowsInterop.GrantDirectoryAccess(config.PluginDirectory, appContainerSid, FileSystemRights.ReadAndExecute);
        // Only private storage and scratch are writable by the AppContainer. Host-owned output
        // staging and snapshot roots deliberately receive no ACE, including from their parents.
        WindowsInterop.GrantDirectoryAccess(storageDirectory, appContainerSid, FileSystemRights.Modify);
        WindowsInterop.GrantDirectoryAccess(tempDirectory, appContainerSid, FileSystemRights.Modify);
        WindowsInterop.DenyDirectoryAccess(hostOutputDirectory, appContainerSid, FileSystemRights.FullControl);
        WindowsInterop.DenyDirectoryAccess(hostSnapshotDirectory, appContainerSid, FileSystemRights.FullControl);
        GrantHostRuntimeReadAccess(appContainerSid);

        System.IO.Pipes.AnonymousPipeServerStream stdinWriter = new(System.IO.Pipes.PipeDirection.Out, System.IO.HandleInheritability.Inheritable);

        IntPtr job = WindowsInterop.CreateJobObjectW(IntPtr.Zero, null);
        WindowsInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new()
        {
            BasicLimitInformation = new WindowsInterop.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = WindowsInterop.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    | WindowsInterop.JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                    | WindowsInterop.JOB_OBJECT_LIMIT_PROCESS_MEMORY,
                ActiveProcessLimit = 1,
            },
            ProcessMemoryLimit = (UIntPtr)Math.Max((ulong)processMemoryLimitBytes, 64UL * 1024 * 1024),
        };
        if (!WindowsInterop.SetInformationJobObject(job, WindowsInterop.JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<WindowsInterop.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            stdinWriter.Dispose();
            WindowsInterop.CloseHandle(job);
            throw new InvalidOperationException($"Job object configuration failed ({Marshal.GetLastWin32Error()}).");
        }

        IntPtr attributeListSize = IntPtr.Zero;
        WindowsInterop.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        IntPtr attributeList = Marshal.AllocHGlobal(attributeListSize);
        _pinnedMemory.Add(attributeList);
        try
        {
            if (!WindowsInterop.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new InvalidOperationException($"Attribute list initialization failed ({Marshal.GetLastWin32Error()}).");
            }

            byte[] sidBytes = new byte[appContainerSid.BinaryLength];
            appContainerSid.GetBinaryForm(sidBytes, 0);
            IntPtr pSid = Marshal.AllocHGlobal(sidBytes.Length);
            Marshal.Copy(sidBytes, 0, pSid, sidBytes.Length);
            _pinnedMemory.Add(pSid);

            WindowsInterop.SECURITY_CAPABILITIES capabilities = new()
            {
                AppContainerSid = pSid,
                Capabilities = IntPtr.Zero,
                CapabilityCount = 0,
            };
            IntPtr pCapabilities = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsInterop.SECURITY_CAPABILITIES>());
            Marshal.StructureToPtr(capabilities, pCapabilities, false);
            _pinnedMemory.Add(pCapabilities);
            if (!WindowsInterop.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)WindowsInterop.ProcThreadAttributeSecurityCapabilities,
                    pCapabilities,
                    (IntPtr)Marshal.SizeOf<WindowsInterop.SECURITY_CAPABILITIES>(),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new InvalidOperationException($"AppContainer attribute rejected ({Marshal.GetLastWin32Error()}); refusing to run the plugin unrestricted.");
            }

            WindowsInterop.STARTUPINFOW startupInfo = new()
            {
                cb = (uint)(Marshal.SizeOf<WindowsInterop.STARTUPINFOW>() + IntPtr.Size),
                dwFlags = WindowsInterop.STARTF_USESTDHANDLES,
                hStdInput = stdinWriter.ClientSafePipeHandle.DangerousGetHandle(),
            };
            IntPtr pStartupInfo = Marshal.AllocHGlobal((int)startupInfo.cb);
            Marshal.StructureToPtr(startupInfo, pStartupInfo, false);
            Marshal.WriteIntPtr(pStartupInfo, Marshal.SizeOf<WindowsInterop.STARTUPINFOW>(), attributeList);
            _pinnedMemory.Add(pStartupInfo);

            string commandLine = $"\"{_hostExecutablePath}\" --plugin-worker";
            if (!WindowsInterop.CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    WindowsInterop.CREATE_SUSPENDED | WindowsInterop.EXTENDED_STARTUPINFO_PRESENT | WindowsInterop.CREATE_UNICODE_ENVIRONMENT | WindowsInterop.CREATE_NO_WINDOW,
                    IntPtr.Zero,
                    Path.GetDirectoryName(_hostExecutablePath),
                    pStartupInfo,
                    out WindowsInterop.PROCESS_INFORMATION process))
            {
                throw new InvalidOperationException($"Worker process creation failed ({Marshal.GetLastWin32Error()}).");
            }

            if (!WindowsInterop.AssignProcessToJobObject(job, process.hProcess))
            {
                WindowsInterop.TerminateJobObject(job, 1);
                WindowsInterop.CloseHandle(process.hProcess);
                throw new InvalidOperationException($"Job assignment failed ({Marshal.GetLastWin32Error()}).");
            }

            VerifyAppContainerToken(process.hProcess);

            stdinWriter.DisposeLocalCopyOfClientHandle();
            WriteStartupJson(stdinWriter, new Dictionary<string, object?>
            {
                ["pipeName"] = pipeName,
                ["credential"] = credential,
                ["sessionId"] = sessionId,
                ["activationId"] = activationId,
                ["pluginDirectory"] = config.PluginDirectory,
                ["entryAssembly"] = config.EntryAssembly,
                ["entryType"] = config.EntryType,
                ["pluginId"] = config.PluginId,
                ["pluginVersion"] = config.PluginVersion,
                ["packageDigest"] = config.PackageDigest,
                ["hostVersion"] = config.HostVersion,
                ["culture"] = config.Culture,
                ["capabilities"] = config.Capabilities,
                ["permissions"] = config.Permissions,
                ["limits"] = config.Limits,
            });

            WindowsInterop.ResumeThread(process.hThread);
            WindowsInterop.CloseHandle(process.hThread);

            return new WorkerSpawnResult
            {
                ProcessHandle = process.hProcess,
                JobHandle = job,
                ProcessId = process.dwProcessId,
                PipeName = pipeName,
                Credential = credential,
                SessionId = sessionId,
                ActivationId = activationId,
                AppContainerSid = appContainerSid.Value,
            };
        }
        catch
        {
            WindowsInterop.CloseHandle(job);
            stdinWriter.Dispose();
            throw;
        }
    }

    private static void VerifyAppContainerToken(IntPtr processHandle)
    {
        if (!WindowsInterop.OpenProcessToken(processHandle, WindowsInterop.TokenQuery, out IntPtr token))
        {
            throw new InvalidOperationException("Cannot open the worker token for sandbox verification; refusing to run the plugin unrestricted.");
        }

        try
        {
            WindowsInterop.GetTokenInformation(token, WindowsInterop.TokenIsAppContainerClass, IntPtr.Zero, 0, out int needed);
            IntPtr buffer = Marshal.AllocHGlobal(4);
            try
            {
                if (!WindowsInterop.GetTokenInformation(token, WindowsInterop.TokenIsAppContainerClass, buffer, 4, out _)
                    || Marshal.ReadInt32(buffer) == 0)
                {
                    throw new InvalidOperationException("The worker token is not an AppContainer token; refusing to run the plugin unrestricted.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            WindowsInterop.CloseHandle(token);
        }
    }

    private static void WriteStartupJson(System.IO.Pipes.AnonymousPipeServerStream stdin, Dictionary<string, object?> values)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(values, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        stdin.Write(payload, 0, payload.Length);
        stdin.Flush();
        stdin.Dispose();
    }

    private void GrantHostRuntimeReadAccess(SecurityIdentifier appContainerSid)
    {
        string baseDirectory = AppContext.BaseDirectory;
        WindowsInterop.GrantFileAccess(_hostExecutablePath, appContainerSid, FileSystemRights.ReadAndExecute);
        foreach (string file in Directory.EnumerateFiles(baseDirectory))
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".dll" or ".json" or ".exe")
            {
                try
                {
                    WindowsInterop.GrantFileAccess(file, appContainerSid, FileSystemRights.ReadAndExecute);
                }
                catch (Exception)
                {
                }
            }
        }

        // Framework-dependent hosts (Debug builds) load the runtime from the shared dotnet
        // install, whose default ACL does not include AppContainer read access. Self-contained
        // single-file releases embed the runtime and skip this entirely. The grant is a single
        // read-and-execute ACE for this plugin's AppContainer SID and is idempotent.
        if (File.Exists(Path.Combine(baseDirectory, "DuCom.dll")))
        {
            string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (string.IsNullOrEmpty(dotnetRoot) || !Directory.Exists(dotnetRoot))
            {
                dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
            }

            if (Directory.Exists(dotnetRoot))
            {
                GrantDirectoryReadOnce(dotnetRoot, appContainerSid);
            }
        }
    }

    private static void GrantDirectoryReadOnce(string directory, SecurityIdentifier sid)
    {
        try
        {
            DirectoryInfo info = new(directory);
            DirectorySecurity security = info.GetAccessControl(AccessControlSections.Access);
            AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule existing in rules.OfType<FileSystemAccessRule>())
            {
                if (existing.IdentityReference is SecurityIdentifier existingSid
                    && existingSid.Value == sid.Value
                    && existing.AccessControlType == AccessControlType.Allow
                    && (existing.FileSystemRights & FileSystemRights.ReadAndExecute) != 0)
                {
                    return;
                }
            }

            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        foreach (IntPtr memory in _pinnedMemory)
        {
            Marshal.FreeHGlobal(memory);
        }

        _pinnedMemory.Clear();
    }
}


