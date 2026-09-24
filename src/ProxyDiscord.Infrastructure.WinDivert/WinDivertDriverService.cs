using System.ComponentModel;
using System.Runtime.InteropServices;
using ProxyDiscord.Infrastructure.Routing;

namespace ProxyDiscord.Infrastructure.WinDivert;

internal static class WinDivertDriverService
{
    private const string SERVICE_NAME = "WinDivert";

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_STOP = 0x0020;
    private const uint DELETE = 0x00010000;
    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_STOPPED = 0x00000001;

    private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    private static readonly TimeSpan STOP_TIMEOUT = TimeSpan.FromSeconds(5);

    public static WinDivertDriverUnloadResult TryUnload()
    {
        var manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
        {
            return Failed("abrir o gerenciador de serviços", Marshal.GetLastWin32Error());
        }

        try
        {
            var service = OpenService(manager, SERVICE_NAME,
                SERVICE_QUERY_STATUS | SERVICE_STOP | DELETE);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return error == ERROR_SERVICE_DOES_NOT_EXIST
                    ? new(true, "WinDivert já estava descarregado e sem serviço registrado.")
                    : Failed("abrir o serviço WinDivert", error);
            }

            try
            {
                return StopAndDelete(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static WinDivertDriverUnloadResult StopAndDelete(IntPtr service)
    {
        if (!QueryServiceStatus(service, out var status))
        {
            return Failed("consultar o estado do WinDivert", Marshal.GetLastWin32Error());
        }

        if (status.CurrentState != SERVICE_STOPPED)
        {
            if (!ControlService(service, SERVICE_CONTROL_STOP, out status))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != ERROR_SERVICE_NOT_ACTIVE)
                {
                    return Failed("parar o WinDivert; outro aplicativo ainda pode usá-lo", error);
                }
            }

            if (!WaitUntilStopped(service))
            {
                return new(false,
                    $"O serviço WinDivert não parou em {STOP_TIMEOUT.TotalSeconds:g}s; " +
                    "outro aplicativo ainda pode estar usando o driver.");
            }
        }

        if (DeleteService(service))
        {
            return new(true, "Driver WinDivert parado e serviço removido do sistema.");
        }

        var deleteError = Marshal.GetLastWin32Error();
        return deleteError is ERROR_SERVICE_MARKED_FOR_DELETE or ERROR_SERVICE_DOES_NOT_EXIST
            ? new(true, "Driver WinDivert parado; o serviço já estava removido ou marcado para remoção.")
            : Failed("remover o serviço WinDivert", deleteError);
    }

    private static bool WaitUntilStopped(IntPtr service)
    {
        var deadline = DateTime.UtcNow + STOP_TIMEOUT;
        while (DateTime.UtcNow < deadline)
        {
            if (!QueryServiceStatus(service, out var status))
            {
                return Marshal.GetLastWin32Error() == ERROR_SERVICE_DOES_NOT_EXIST;
            }

            if (status.CurrentState == SERVICE_STOPPED)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static WinDivertDriverUnloadResult Failed(string operation, int error) =>
        new(false, $"Não foi possível {operation} (erro {error}: {new Win32Exception(error).Message}).");

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr serviceManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
