namespace CccPrint.Models;

public sealed record SourceFileSnapshot(
    string RelativePath,
    string LibraryId,
    string LibraryName,
    string Extension,
    long Length,
    DateTimeOffset LastWriteTimeUtc);

public sealed record SourceBaseline(
    int SchemaVersion,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<SourceFileSnapshot> Files);

public sealed record SourceFileChange(
    string Kind,
    string RelativePath,
    string LibraryId,
    string LibraryName,
    string Extension,
    long? PreviousLength,
    long? CurrentLength,
    DateTimeOffset? PreviousWriteTimeUtc,
    DateTimeOffset? CurrentWriteTimeUtc);

public sealed record SourceChangeSet(
    DateTimeOffset ScannedAtUtc,
    DateTimeOffset BaselineCreatedAtUtc,
    int CurrentFileCount,
    int AddedCount,
    int ModifiedCount,
    int RemovedCount,
    IReadOnlyList<SourceFileChange> Changes);
