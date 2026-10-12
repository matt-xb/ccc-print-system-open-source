namespace CccPrint.Models;

public sealed record LibrarySummary(
    string Id,
    string Name,
    bool IsAvailable,
    int SourceFileCount,
    int PrintableCandidateCount,
    long TotalBytes,
    DateTimeOffset? LatestWriteTime);

