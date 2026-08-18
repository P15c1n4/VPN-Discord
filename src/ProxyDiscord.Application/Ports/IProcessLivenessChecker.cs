namespace ProxyDiscord.Application.Ports;

public interface IProcessLivenessChecker
{
    (int Pid, DateTime StartedUtc) GetCurrentProcessInfo();

    bool IsSameProcessStillRunning(int pid, DateTime expectedStartUtc);
}
