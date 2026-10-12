namespace CccPrint.Networking;

public sealed record AppUpdateManifest(
    bool Available,
    string? Version,
    string? PackageFileName,
    string? DownloadUrl,
    long? Bytes,
    string? Sha256,
    string? ReleaseNotes,
    DateTimeOffset? PublishedAtUtc,
    string? Message);
