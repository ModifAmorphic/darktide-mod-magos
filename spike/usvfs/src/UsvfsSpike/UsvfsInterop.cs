using System.Runtime.InteropServices;
using System.Text;

namespace UsvfsSpike;

// P/Invoke over usvfs_x64.dll. The API is `extern "C"` WINAPI (__stdcall).
// `usvfsParameters` is opaque (forward-declared in usvfsparameters.h); we obtain
// it via usvfsCreateParameters() and configure it via the setters, so it is
// marshaled as IntPtr (no struct layout needed).
internal static class UsvfsInterop
{
    private const string Dll = "usvfs_x64.dll";

    // usvfsParameters lifecycle ------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr usvfsCreateParameters();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void usvfsFreeParameters(IntPtr p);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall, EntryPoint = "usvfsSetInstanceName")]
    public static extern void usvfsSetInstanceName(IntPtr p, [MarshalAs(UnmanagedType.LPStr)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void usvfsSetDebugMode(IntPtr p, [MarshalAs(UnmanagedType.Bool)] bool debugMode);

    // LogLevel is uint8 { Debug=0, Info=1, Warning=2, Error=3 }
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void usvfsSetLogLevel(IntPtr p, byte level);

    // VFS session + mappings ---------------------------------------------------

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool usvfsCreateVFS(IntPtr p);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool usvfsConnectVFS(IntPtr p);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void usvfsDisconnectVFS();

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void usvfsClearVirtualMappings();

    // Virtual-link a real source directory onto a virtual destination. Both wide.
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool usvfsVirtualLinkDirectoryStatic(
        [MarshalAs(UnmanagedType.LPWStr)] string source,
        [MarshalAs(UnmanagedType.LPWStr)] string destination,
        uint flags);

    // Spawn a process that sees the VFS. Signature mirrors CreateProcessW.
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool usvfsCreateProcessHooked(
        [MarshalAs(UnmanagedType.LPWStr)] string? lpApplicationName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    // Diagnostics --------------------------------------------------------------

    // Initialize USVFS logging. toLocal=true logs to a local file (next to the dll/
    // in ProgramData) instead of just the in-memory ring buffer. Call once at start.
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void usvfsInitLogging([MarshalAs(UnmanagedType.Bool)] bool toLocal);

    // LPSTR (ANSI) buffer, size_t size, C++ bool blocking. blocking marshaled as I1.
    // Use byte[] (decoded ANSI) to avoid StringBuilder CharSet ambiguity.
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool usvfsGetLogMessages(byte[] buffer, ulong size, [MarshalAs(UnmanagedType.I1)] bool blocking);

    // const char* (ANSI) -> marshal manually.
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr usvfsVersionString();

    // LINKFLAG_* constants from usvfs.h
    public const uint LINKFLAG_FAILIFEXISTS = 0x00000001;
    public const uint LINKFLAG_MONITORCHANGES = 0x00000002;
    public const uint LINKFLAG_CREATETARGET = 0x00000004;
    public const uint LINKFLAG_RECURSIVE = 0x00000008;
    public const uint LINKFLAG_FAILIFSKIPPED = 0x00000010;

    // kernel32: used by the relay-standin to mirror relay's plain CreateProcess(suspended)+resume.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    public const uint CREATE_SUSPENDED = 0x00000004;
    public const uint CREATE_NEW_CONSOLE = 0x00000010;
    public const uint INFINITE = 0xFFFFFFFF;
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 258;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDesktop;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
