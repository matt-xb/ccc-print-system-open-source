using System.IO;
using CccPrint.Models;
using CccPrint.Networking;

namespace CccPrint.Services;

public sealed class TestLibraryService
{
    private readonly string _dataRoot;
    private readonly DraftPreparationService _drafts;

    public TestLibraryService(
        string dataRoot,
        DraftPreparationService drafts)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _drafts = drafts;
    }

    public async Task<LibrarySyncManifest> GetManifestAsync(
        CancellationToken cancellationToken = default)
    {
        var batch = (await _drafts.GetPendingAsync(cancellationToken))
            .FirstOrDefault(item =>
                item.Status.Equals(
                    "test-only-do-not-publish",
                    StringComparison.OrdinalIgnoreCase));
        if (batch is null)
        {
            return new LibrarySyncManifest(
                1,
                null,
                true,
                DateTimeOffset.UtcNow,
                "当前没有可供开发测试的 PDF 资料。",
                Array.Empty<LibrarySyncFile>());
        }

        var files = batch.Sources
            .Where(source => source.Success)
            .SelectMany(source => source.Outputs.Select(output =>
                ToManifestFile(batch, source, output)))
            .OrderBy(file => file.LibraryName, StringComparer.Ordinal)
            .ThenBy(file => file.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LibrarySyncManifest(
            1,
            $"test-{batch.BatchId}",
            true,
            DateTimeOffset.UtcNow,
            "当前为开发测试资料，部分表格分页尚未调整，不代表正式有效版本。",
            files);
    }

    public async Task<string?> GetFilePathAsync(
        string batchId,
        string fileId,
        CancellationToken cancellationToken = default)
    {
        var batch = (await _drafts.GetPendingAsync(cancellationToken))
            .FirstOrDefault(item =>
                item.BatchId.Equals(
                    batchId,
                    StringComparison.OrdinalIgnoreCase) &&
                item.Status.Equals(
                    "test-only-do-not-publish",
                    StringComparison.OrdinalIgnoreCase));
        var output = batch?.Sources
            .SelectMany(source => source.Outputs)
            .FirstOrDefault(item =>
                item.OutputId.Equals(
                    fileId,
                    StringComparison.OrdinalIgnoreCase));
        if (output is null)
        {
            return null;
        }

        var path = Path.GetFullPath(
            Path.Combine(
                _dataRoot,
                output.RelativePdfPath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        var rootPrefix =
            Path.TrimEndingDirectorySeparator(_dataRoot) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(
                   rootPrefix,
                   StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path)
            ? path
            : null;
    }

    private static LibrarySyncFile ToManifestFile(
        DraftBatch batch,
        DraftSourceItem source,
        PdfDraftOutput output)
    {
        var (libraryId, libraryName) =
            GetLibrary(source.RelativePath);
        return new LibrarySyncFile(
            output.OutputId,
            libraryId,
            libraryName,
            source.RelativePath,
            output.DisplayName,
            output.WorksheetName,
            output.WorksheetIndex,
            output.PageCount,
            output.Bytes,
            output.Sha256,
            output.RequiresReview,
            $"/api/v1/library/test-files/{batch.BatchId}/{output.OutputId}");
    }

    private static (string Id, string Name) GetLibrary(
        string relativePath)
    {
        if (relativePath.StartsWith(
                "CCC燃气资料/",
                StringComparison.OrdinalIgnoreCase))
        {
            return ("ccc-gas", "CCC 燃气资料库");
        }

        if (relativePath.StartsWith(
                "电加热资料/",
                StringComparison.OrdinalIgnoreCase))
        {
            return ("electric-heating", "电加热资料库");
        }

        if (relativePath.StartsWith(
                "政府资料/",
                StringComparison.OrdinalIgnoreCase))
        {
            return ("government", "政府资料库");
        }

        return ("other", "其他资料");
    }
}
