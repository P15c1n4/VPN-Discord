namespace ProxyDiscord.Application.Dtos;

public sealed record UpdatePackageInfo(
    string AssetName,
    Uri DownloadUri,
    long SizeBytes,
    string? Sha256Digest);

public sealed record UpdateReleaseInfo(
    string TagName,
    Version Version,
    string ReleaseName,
    string ReleaseNotes,
    Uri ReleasePageUri,
    UpdatePackageInfo? Package);

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    UpdateReleaseInfo? LatestRelease)
{
    public bool IsUpdateAvailable => LatestRelease is { } release && release.Version > CurrentVersion;
}
