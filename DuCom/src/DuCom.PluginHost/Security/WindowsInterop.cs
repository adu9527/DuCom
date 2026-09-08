using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DuCom.PluginHost.Security;

internal static class WindowsInterop
{
    internal const uint CREATE_SUSPENDED = 0x4;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000;
    internal const uint CREATE_NO_WINDOW = 0x8000000;
    internal const uint STARTF_USESTDHANDLES = 0x100;
    internal const int ProcThreadAttributeSecurityCapabilities = 0x00020009;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x8;
    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    internal const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool CreateProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        IntPtr startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    internal static extern ushort ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, int informationLength, out int returnLength);

    internal const uint TokenQuery = 0x8;
    internal const int TokenIsAppContainerClass = 29;

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int CreateAppContainerProfile(string moniker, string displayName, string description, IntPtr capabilities, uint capabilityCount, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeriveAppContainerSidFromAppContainerName(string moniker, out IntPtr sid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    internal static extern int DeleteAppContainerProfile(string moniker);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllocateAndInitializeSid(byte[] identifierAuthority, byte subAuthorityCount, uint s0, uint s1, uint s2, uint s3, uint s4, uint s5, uint s6, uint s7, out IntPtr sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_CAPABILITIES
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public byte PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    internal static string SidToString(IntPtr sid)
    {
        if (ConvertSidToStringSidW(sid, out IntPtr p))
        {
            try
            {
                return Marshal.PtrToStringUni(p) ?? string.Empty;
            }
            finally
            {
                LocalFree(p);
            }
        }

        return string.Empty;
    }

    internal static void GrantDirectoryAccess(string path, SecurityIdentifier sid, FileSystemRights rights)
    {
        DirectoryInfo info = new(path);
        DirectorySecurity security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    internal static void DenyDirectoryAccess(string path, SecurityIdentifier sid, FileSystemRights rights)
    {
        DirectoryInfo info = new(path);
        DirectorySecurity security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    internal static void GrantFileAccess(string path, SecurityIdentifier sid, FileSystemRights rights)
    {
        FileInfo info = new(path);
        FileSecurity security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
