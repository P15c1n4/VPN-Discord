namespace ProxyDiscord.Application.Dtos;

public sealed record ConnectionStateRecord(
    int OwnerPid,
    DateTime OwnerStartedUtc,
    int TargetProcessId,
    string TargetProcessName,
    string RasEntryName,
    DateTime CreatedUtc);
