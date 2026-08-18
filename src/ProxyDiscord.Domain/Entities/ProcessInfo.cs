namespace ProxyDiscord.Domain.Entities;

public sealed record ProcessInfo(int Pid, string Name, string? ExecutablePath);
