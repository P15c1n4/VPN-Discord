namespace ProxyDiscord.Application.Dtos;

public sealed record TargetProcessSelector(string ProcessName, string? ExecutablePath)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ExecutablePath)
        ? ProcessName
        : Path.GetFileName(ExecutablePath);
}
