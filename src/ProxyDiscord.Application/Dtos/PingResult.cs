namespace ProxyDiscord.Application.Dtos;

public sealed record HostProbe(string Id, string Host);

public sealed record PingResult(string Id, bool Success, TimeSpan? Latency, string? FailureReason);
