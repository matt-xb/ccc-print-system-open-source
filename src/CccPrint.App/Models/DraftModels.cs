namespace CccPrint.Models;

public sealed record PrepareDraftRequest(
    IReadOnlyList<string> RelativePaths);

public sealed record PdfDraftOutput(
    string OutputId,
    string DisplayName,
    string? WorksheetName,
    int? WorksheetIndex,
    string RelativePdfPath,
    long Bytes,
    string Sha256,
    int PageCount,
    bool HasMixedPageSizes,
    bool RequiresReview,
    IReadOnlyList<string> ReviewReasons);

public sealed record DraftSourceItem(
    string SourceId,
    string RelativePath,
    string Extension,
    long SourceBytes,
    string SourceSha256,
    bool Success,
    long ElapsedMilliseconds,
    IReadOnlyList<PdfDraftOutput> Outputs,
    string? Error);

public sealed record DraftBatch(
    int SchemaVersion,
    string BatchId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    int RequestedSourceCount,
    int SucceededSourceCount,
    int FailedSourceCount,
    int PdfCount,
    int ReviewRequiredCount,
    IReadOnlyList<DraftSourceItem> Sources)
{
    public IReadOnlyList<string> RemovedSourcePaths { get; init; } =
        Array.Empty<string>();
}

public sealed record WpsGeneratedPdf(
    string FilePath,
    string DisplayName,
    string? WorksheetName,
    int? WorksheetIndex);

public sealed record PdfInspection(
    long Bytes,
    string Sha256,
    int PageCount,
    bool HasMixedPageSizes);
