using System.Diagnostics;
using ProxyDiscord.Infrastructure.ProcessManagement;

namespace ProxyDiscord.Infrastructure.ProcessManagement.Tests;

public class Win32ProcessLivenessCheckerTests
{
    [Fact]
    public void GetCurrentProcessInfo_ReturnsThisProcessPidAndStartTime()
    {
        var checker = new Win32ProcessLivenessChecker();

        var (pid, startedUtc) = checker.GetCurrentProcessInfo();

        Assert.Equal(Environment.ProcessId, pid);
        using var expected = Process.GetCurrentProcess();
        Assert.Equal(expected.StartTime.ToUniversalTime(), startedUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void IsSameProcessStillRunning_CurrentProcessWithMatchingStartTime_ReturnsTrue()
    {
        var checker = new Win32ProcessLivenessChecker();
        var (pid, startedUtc) = checker.GetCurrentProcessInfo();

        Assert.True(checker.IsSameProcessStillRunning(pid, startedUtc));
    }

    [Fact]
    public void IsSameProcessStillRunning_NonExistentPid_ReturnsFalse()
    {
        var checker = new Win32ProcessLivenessChecker();

        Assert.False(checker.IsSameProcessStillRunning(999_999, DateTime.UtcNow));
    }

    [Fact]
    public void IsSameProcessStillRunning_SamePidButWrongStartTime_ReturnsFalse()
    {
        var checker = new Win32ProcessLivenessChecker();
        var (pid, _) = checker.GetCurrentProcessInfo();

        Assert.False(checker.IsSameProcessStillRunning(pid, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }
}
