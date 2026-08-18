using ProxyDiscord.Domain.Entities;

namespace ProxyDiscord.Presentation.Wpf.ViewModels;

public sealed class ProcessGroupViewModel
{
    public ProcessGroupViewModel(string name, string? executablePath, IReadOnlyList<ProcessInfo> instances)
    {
        Name = name;
        ExecutablePath = executablePath;
        Instances = instances;
    }

    public string Name { get; }

    public string? ExecutablePath { get; }

    public IReadOnlyList<ProcessInfo> Instances { get; }

    public string InstanceText => Instances.Count > 1 ? $"({Instances.Count})" : "";

    public string PathText => ExecutablePath ?? "caminho indisponível";

    public ProcessInfo Representative =>
        Instances.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ExecutablePath)) ?? Instances[0];
}
