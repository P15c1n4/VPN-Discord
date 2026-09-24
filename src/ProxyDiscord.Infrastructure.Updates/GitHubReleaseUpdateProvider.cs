using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProxyDiscord.Application.Dtos;
using ProxyDiscord.Application.Ports;

namespace ProxyDiscord.Infrastructure.Updates;

public sealed class GitHubReleaseUpdateProvider(HttpClient httpClient) : IReleaseUpdateProvider
{
    private const string LATEST_RELEASE_URL =
        "https://api.github.com/repos/P15c1n4/VPN-Discord/releases/latest";
    private const string PACKAGE_ASSET_NAME = "Discord-VPN-win-x64.zip";

    public async Task<UpdateReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LATEST_RELEASE_URL);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ProxyDiscord", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<GitHubReleasePayload>(cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.TagName))
        {
            throw new InvalidDataException("O GitHub não informou a tag da versão publicada.");
        }

        var versionText = payload.TagName.Trim();
        if (versionText.StartsWith("Version-", StringComparison.OrdinalIgnoreCase))
        {
            versionText = versionText["Version-".Length..];
        }
        else if (versionText.StartsWith('v') || versionText.StartsWith('V'))
        {
            versionText = versionText[1..];
        }

        if (!Version.TryParse(versionText, out var version))
        {
            throw new InvalidDataException(
                $"A tag '{payload.TagName}' não segue o formato de versão esperado (ex.: Version-1.2.3).");
        }

        if (!Uri.TryCreate(payload.HtmlUrl, UriKind.Absolute, out var releasePage) ||
            releasePage.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(releasePage.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O GitHub retornou um endereço de versão inválido.");
        }

        var asset = payload.Assets?.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, PACKAGE_ASSET_NAME, StringComparison.Ordinal) &&
            string.Equals(candidate.State, "uploaded", StringComparison.OrdinalIgnoreCase));

        var package = asset is null ? null : MapPackage(asset);
        return new UpdateReleaseInfo(
            payload.TagName,
            version,
            string.IsNullOrWhiteSpace(payload.Name) ? payload.TagName : payload.Name,
            payload.Body ?? "",
            releasePage,
            package);
    }

    private static UpdatePackageInfo MapPackage(GitHubReleaseAsset asset)
    {
        if (asset.Size <= 0 ||
            !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("O pacote de atualização publicado no GitHub é inválido.");
        }

        if (!string.IsNullOrWhiteSpace(asset.Digest) &&
            (!asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
             asset.Digest.Length != "sha256:".Length + 64 ||
             !asset.Digest["sha256:".Length..].All(Uri.IsHexDigit)))
        {
            throw new InvalidDataException("O hash SHA-256 do pacote publicado é inválido.");
        }

        return new UpdatePackageInfo(PACKAGE_ASSET_NAME, downloadUri, asset.Size, asset.Digest);
    }

    private sealed record GitHubReleasePayload(
        [property: JsonPropertyName("tag_name")] string? TagName,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] GitHubReleaseAsset[]? Assets);

    private sealed record GitHubReleaseAsset(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("browser_download_url")] string? BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);
}
