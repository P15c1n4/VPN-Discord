using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ProxyDiscord.Infrastructure.Routing;

internal static class HiddenConsoleProcess
{
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const ushort SW_HIDE = 0;
    private const uint CTRL_BREAK_EVENT = 1;

    public static Process Start(string executable, string workingDirectory)
    {
        var startup = new StartupInfo
        {
            Size = Marshal.SizeOf<StartupInfo>(),
            Flags = STARTF_USESHOWWINDOW,
            ShowWindow = SW_HIDE,
        };
        var commandLine = new StringBuilder($"\"{executable}\"");

        if (!CreateProcess(
                executable,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP,
                IntPtr.Zero,
                workingDirectory,
                ref startup,
                out var processInfo))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Não foi possível iniciar o ProxiFyre.");
        }

        CloseHandle(processInfo.Thread);
        CloseHandle(processInfo.Process);
        return Process.GetProcessById((int)processInfo.ProcessId);
    }

    public static bool SendCtrlBreak(int processId)
    {
        if (!AttachConsole((uint)processId)) return false;
        try
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            return GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)processId);
        }
        finally
        {
            FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
