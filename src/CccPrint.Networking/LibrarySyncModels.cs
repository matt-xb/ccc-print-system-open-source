namespace CccPrint.Networking;

public sealed record LibrarySyncManifest(
    int SchemaVersion,
    string? Version,
    bool IsTestData,
    DateTimeOffset GeneratedAtUtc,
    string Notice,
    IReadOnlyList<LibrarySyncFile> Files)
{
    public IReadOnlyList<string> RemovedSourcePaths { get; init; } =
        Array.Empty<string>();
}

public sealed record LibrarySyncFile(
    string FileId,
    string LibraryId,
    string LibraryName,
    string SourceRelativePath,
    string DisplayName,
    string? WorksheetName,
    int? WorksheetIndex,
    int PageCount,
    long Bytes,
    string Sha256,
    bool RequiresReview,
    string DownloadUrl);

public sealed record LibrarySyncProgress(
    int CompletedFiles,
    int TotalFiles,
    string CurrentFile,
    double CurrentFileProgress);

public sealed record LibrarySyncStatus(
    string? Version,
    bool IsTestData,
    DateTimeOffset GeneratedAtUtc,
    int FileCount,
    string Notice);

public sealed record ClientLibraryInventory(
    string DeviceName,
    string ClientVersion,
    DateTimeOffset ReportedAtUtc,
    LibrarySyncManifest? LocalManifest);
