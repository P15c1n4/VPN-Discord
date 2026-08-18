using System.Diagnostics;
using ProxyDiscord.Application.Ports;
using ProxyDiscord.Domain.Entities;

namespace ProxyDiscord.Infrastructure.ProcessManagement;

public sealed class Win32ProcessRepository : IProcessRepository
{
    public Task<IReadOnlyList<ProcessInfo>> GetRunningProcessesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var result = new List<ProcessInfo>();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                    }

                    result.Add(new ProcessInfo(process.Id, process.ProcessName, path));
                }
            }

            IReadOnlyList<ProcessInfo> distinct = result
                .GroupBy(p => p.Pid)
                .Select(g => g.First())
                .ToList();

            return distinct;
        }, cancellationToken);
    }
}
