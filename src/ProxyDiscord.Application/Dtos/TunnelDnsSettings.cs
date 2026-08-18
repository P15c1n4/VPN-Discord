namespace ProxyDiscord.Application.Dtos;

public sealed record TunnelDnsSettings(string ServerIp)
{
    public const string GOOGLE_PUBLIC_DNS = "8.8.8.8";
    public const string CLOUDFLARE_PUBLIC_DNS = "1.1.1.1";

    public static TunnelDnsSettings Default { get; } = new(GOOGLE_PUBLIC_DNS);

    public static IReadOnlyList<string> Suggestions { get; } = [GOOGLE_PUBLIC_DNS, CLOUDFLARE_PUBLIC_DNS];
}
