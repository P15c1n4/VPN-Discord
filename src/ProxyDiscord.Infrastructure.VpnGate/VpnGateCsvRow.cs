namespace ProxyDiscord.Infrastructure.VpnGate;

internal sealed record VpnGateCsvRow(
    string HostName,
    string Ip,
    string Score,
    string PingMs,
    string SpeedBps,
    string CountryLong,
    string CountryShort,
    string NumVpnSessions,
    string Uptime,
    string TotalUsers,
    string TotalTraffic,
    string LogType,
    string Operator,
    string Message,
    string OpenVpnConfigBase64);
