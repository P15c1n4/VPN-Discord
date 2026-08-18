using System.Diagnostics;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.ProcessManagement;

public sealed class Win32ProcessLivenessChecker : IProcessLivenessChecker
{
    private static readonly TimeSpan START_TIME_TOLERANCE = TimeSpan.FromSeconds(2);

    public (int Pid, DateTime StartedUtc) GetCurrentProcessInfo()
    {
        using var current = Process.GetCurrentProcess();
        return (current.Id, current.StartTime.ToUniversalTime());
    }

    public bool IsSameProcessStillRunning(int pid, DateTime expectedStartUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var actualStartUtc = process.StartTime.ToUniversalTime();
            return (actualStartUtc - expectedStartUtc).Duration() <= START_TIME_TOLERANCE;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
