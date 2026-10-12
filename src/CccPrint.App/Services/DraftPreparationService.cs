using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CccPrint.Models;

namespace CccPrint.Services;

public sealed class DraftPreparationService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc",
            ".docx",
            ".xls",
            ".xlsx",
            ".pdf"
        };

    private static readonly HashSet<string> WpsExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc",
            ".docx",
            ".xls",
            ".xlsx"
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _sourceRoot;
    private readonly string _dataRoot;
    private readonly string _draftsRoot;
    private readonly string _workRoot;
    private readonly WpsConversionService _wpsConverter = new();
    private readonly PdfInspectionService _pdfInspector = new();
    private readonly SemaphoreSlim _batchGate = new(1, 1);

    public DraftPreparationService(string sourceRoot, string dataRoot)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _dataRoot = Path.GetFullPath(dataRoot);
        _draftsRoot = Path.Combine(_dataRoot, "drafts");
        _workRoot = Path.Combine(_dataRoot, "work");
    }

    public async Task<DraftBatch> PrepareAsync(
        IReadOnlyList<string> relativePaths,
        IReadOnlyList<string>? removedRelativePaths = null,
        CancellationToken cancellationToken = default)
    {
        var sources = relativePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ResolveSource)
            .ToArray();

        var removals = (removedRelativePaths ?? Array.Empty<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sources.Length == 0 && removals.Length == 0)
        {
            throw new ArgumentException("至少选择一项资料变更。", nameof(relativePaths));
        }

        if (sources.Length + removals.Length > 20)
        {
            throw new ArgumentException(
                "单个待发布批次最多处理 20 份源资料。",
                nameof(relativePaths));
        }

        if (sources.Any(source => WpsExtensions.Contains(source.Extension)))
        {
            var visibleWps = WpsConversionService.FindVisibleWpsProcesses();
            if (visibleWps.Count > 0)
            {
                throw new WpsBusyException(
                    $"检测到正在使用的 WPS 窗口：{string.Join("、", visibleWps)}。" +
                    "请保存并关闭可见的 WPS 文档后再转换。");
            }
        }

        await _batchGate.WaitAsync(cancellationToken);
        try
        {
            return await PrepareCoreAsync(
                sources,
                removals,
                cancellationToken);
        }
        finally
        {
            _batchGate.Release();
        }
    }

    public async Task<IReadOnlyList<DraftBatch>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_draftsRoot))
        {
            return Array.Empty<DraftBatch>();
        }

        var batches = new List<DraftBatch>();
        foreach (var directory in Directory
                     .EnumerateDirectories(_draftsRoot)
                     .Where(path =>
                         !Path.GetFileName(path)
                             .StartsWith(".working-", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, "batch.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(manifestPath);
                var batch = await JsonSerializer.DeserializeAsync<DraftBatch>(
                    stream,
                    JsonOptions,
                    cancellationToken);
                if (batch is not null)
                {
                    batches.Add(batch);
                }
            }
            catch (JsonException)
            {
                // 单个损坏草稿不阻断其他草稿展示。
            }
            catch (IOException)
            {
                // 文件正在写入时下一次重新读取。
            }
        }

        return batches
            .OrderByDescending(batch => batch.CreatedAtUtc)
            .ToArray();
    }

    public async Task<DraftBatch> PublishForTestingAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(batchId) ||
            batchId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("草稿批次编号无效。", nameof(batchId));
        }

        await _batchGate.WaitAsync(cancellationToken);
        try
        {
            var batches = await LoadAllBatchesAsync(cancellationToken);
            var selected = batches.FirstOrDefault(batch =>
                batch.BatchId.Equals(
                    batchId,
                    StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                throw new DirectoryNotFoundException("找不到所选草稿批次。");
            }

            if (selected.PdfCount == 0 ||
                selected.SucceededSourceCount == 0)
            {
                throw new InvalidOperationException(
                    "所选草稿没有可推送的 PDF。");
            }

            DraftBatch? published = null;
            foreach (var batch in batches)
            {
                var nextStatus = batch.BatchId.Equals(
                    batchId,
                    StringComparison.OrdinalIgnoreCase)
                    ? "test-only-do-not-publish"
                    : batch.Status.Equals(
                        "test-only-do-not-publish",
                        StringComparison.OrdinalIgnoreCase)
                        ? "test-archived"
                        : batch.Status;
                if (nextStatus.Equals(
                        batch.Status,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (batch.BatchId.Equals(
                            batchId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        published = batch;
                    }

                    continue;
                }

                var updated = batch with { Status = nextStatus };
                await ReplaceBatchManifestAsync(updated, cancellationToken);
                if (updated.BatchId.Equals(
                        batchId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    published = updated;
                }
            }

            return published ?? selected;
        }
        finally
        {
            _batchGate.Release();
        }
    }

    private async Task<DraftBatch> PrepareCoreAsync(
        IReadOnlyList<ResolvedSource> sources,
        IReadOnlyList<string> removedRelativePaths,
        CancellationToken cancellationToken)
    {
        var batchId =
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..31];
        Directory.CreateDirectory(_draftsRoot);
        Directory.CreateDirectory(_workRoot);

        var workingDraftPath = Path.Combine(
            _draftsRoot,
            $".working-{batchId}");
        var finalDraftPath = Path.Combine(_draftsRoot, batchId);
        var batchWorkPath = Path.Combine(_workRoot, batchId);
        Directory.CreateDirectory(workingDraftPath);
        Directory.CreateDirectory(batchWorkPath);

        var batchMoved = false;
        try
        {
            var results = new List<DraftSourceItem>();
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await PrepareSourceAsync(
                    source,
                    batchWorkPath,
                    workingDraftPath,
                    finalDraftPath,
                    cancellationToken));
            }

            var batch = new DraftBatch(
                1,
                batchId,
                "pending-publish",
                DateTimeOffset.UtcNow,
                sources.Count + removedRelativePaths.Count,
                results.Count(result => result.Success),
                results.Count(result => !result.Success),
                results.Sum(result => result.Outputs.Count),
                results.Sum(result =>
                    result.Outputs.Count(output => output.RequiresReview)),
                results)
            {
                RemovedSourcePaths = removedRelativePaths
            };

            await WriteJsonAsync(
                Path.Combine(workingDraftPath, "batch.json"),
                batch,
                cancellationToken);
            Directory.Move(workingDraftPath, finalDraftPath);
            batchMoved = true;
            return batch;
        }
        finally
        {
            DeleteDirectoryInsideRoot(_workRoot, batchWorkPath);
            if (!batchMoved)
            {
                DeleteDirectoryInsideRoot(_draftsRoot, workingDraftPath);
            }
        }
    }

    private async Task<IReadOnlyList<DraftBatch>> LoadAllBatchesAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_draftsRoot))
        {
            return Array.Empty<DraftBatch>();
        }

        var batches = new List<DraftBatch>();
        foreach (var directory in Directory.EnumerateDirectories(_draftsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory)
                .StartsWith(".working-", StringComparison.Ordinal))
            {
                continue;
            }

            var manifestPath = Path.Combine(directory, "batch.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            await using var stream = File.OpenRead(manifestPath);
            var batch = await JsonSerializer.DeserializeAsync<DraftBatch>(
                stream,
                JsonOptions,
                cancellationToken);
            if (batch is not null)
            {
                batches.Add(batch);
            }
        }

        return batches;
    }

    private async Task ReplaceBatchManifestAsync(
        DraftBatch batch,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(
            Path.Combine(_draftsRoot, batch.BatchId));
        var rootPrefix =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_draftsRoot)) +
            Path.DirectorySeparatorChar;
        if (!directory.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("草稿批次路径无效。");
        }

        var manifestPath = Path.Combine(directory, "batch.json");
        var temporaryPath = Path.Combine(
            directory,
            $".batch.{Guid.NewGuid():N}.tmp");
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         65536,
                         FileOptions.Asynchronous |
                         FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                batch,
                JsonOptions,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, manifestPath, true);
    }

    private async Task<DraftSourceItem> PrepareSourceAsync(
        ResolvedSource source,
        string batchWorkPath,
        string workingDraftPath,
        string finalDraftPath,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var sourceId = CreateStableId(source.RelativePath);
        var inputCopyPath = Path.Combine(
            batchWorkPath,
            $"{sourceId}{source.Extension}");
        var outputDirectory = Path.Combine(
            workingDraftPath,
            "items",
            sourceId);
        var sourceBytes = 0L;
        var sourceHash = string.Empty;

        try
        {
            await WaitForStableFileAsync(source.FullPath, cancellationToken);
            await CopyFileAsync(
                source.FullPath,
                inputCopyPath,
                cancellationToken);
            var inputCopy = new FileInfo(inputCopyPath);
            sourceBytes = inputCopy.Length;
            sourceHash = await ComputeSha256Async(
                inputCopyPath,
                cancellationToken);
            Directory.CreateDirectory(outputDirectory);

            IReadOnlyList<WpsGeneratedPdf> generated;
            if (source.Extension.Equals(
                    ".pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                var outputPath = Path.Combine(
                    outputDirectory,
                    SanitizeFileName(Path.GetFileName(source.FullPath)));
                await CopyFileAsync(
                    inputCopyPath,
                    outputPath,
                    cancellationToken);
                generated =
                [
                    new WpsGeneratedPdf(
                        outputPath,
                        Path.GetFileNameWithoutExtension(source.FullPath),
                        null,
                        null)
                ];
            }
            else
            {
                generated = await _wpsConverter.ConvertAsync(
                    inputCopyPath,
                    outputDirectory,
                    cancellationToken);
            }

            var outputs = new List<PdfDraftOutput>();
            foreach (var pdf in generated)
            {
                var inspection = await _pdfInspector.InspectAsync(
                    pdf.FilePath,
                    cancellationToken);
                var reviewReasons = GetReviewReasons(pdf, inspection);
                var displayName = pdf.WorksheetName is null
                    ? Path.GetFileNameWithoutExtension(source.FullPath)
                    : $"{Path.GetFileNameWithoutExtension(source.FullPath)} / " +
                      pdf.WorksheetName;

                outputs.Add(new PdfDraftOutput(
                    pdf.WorksheetIndex is null
                        ? $"{sourceId}-document"
                        : $"{sourceId}-sheet-{pdf.WorksheetIndex:000}",
                    displayName,
                    pdf.WorksheetName,
                    pdf.WorksheetIndex,
                    NormalizeRelativePath(
                        Path.GetRelativePath(
                            _dataRoot,
                            Path.Combine(
                                finalDraftPath,
                                "items",
                                sourceId,
                                Path.GetFileName(pdf.FilePath)))),
                    inspection.Bytes,
                    inspection.Sha256,
                    inspection.PageCount,
                    inspection.HasMixedPageSizes,
                    reviewReasons.Count > 0,
                    reviewReasons));
            }

            stopwatch.Stop();
            return new DraftSourceItem(
                sourceId,
                source.RelativePath,
                source.Extension,
                sourceBytes,
                sourceHash,
                true,
                stopwatch.ElapsedMilliseconds,
                outputs,
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            DeleteDirectoryInsideRoot(workingDraftPath, outputDirectory);
            return new DraftSourceItem(
                sourceId,
                source.RelativePath,
                source.Extension,
                sourceBytes,
                sourceHash,
                false,
                stopwatch.ElapsedMilliseconds,
                Array.Empty<PdfDraftOutput>(),
                exception.GetBaseException().Message);
        }
    }

    private ResolvedSource ResolveSource(string requestedPath)
    {
        if (Path.IsPathRooted(requestedPath))
        {
            throw new ArgumentException("源资料路径必须是相对路径。");
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(
                _sourceRoot,
                requestedPath.Replace('/', Path.DirectorySeparatorChar)));
        var relativePath = Path.GetRelativePath(_sourceRoot, fullPath);
        if (relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("源资料路径超出配置的资料目录。");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("源资料不存在。", fullPath);
        }

        var extension = Path.GetExtension(fullPath);
        if (!SupportedExtensions.Contains(extension))
        {
            throw new NotSupportedException($"不支持此文件类型：{extension}");
        }

        return new ResolvedSource(
            fullPath,
            NormalizeRelativePath(relativePath),
            extension.ToLowerInvariant());
    }

    private static async Task WaitForStableFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        (long Length, DateTime LastWriteUtc)? previous = null;
        var stableReadings = 0;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                file.Refresh();
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1,
                    FileOptions.Asynchronous);
                var current = (file.Length, file.LastWriteTimeUtc);
                stableReadings = previous == current
                    ? stableReadings + 1
                    : 0;
                previous = current;

                if (stableReadings >= 2)
                {
                    return;
                }
            }
            catch (IOException)
            {
                stableReadings = 0;
                previous = null;
            }

            await Task.Delay(300, cancellationToken);
        }

        throw new IOException($"文件仍在保存或被独占占用：{path}");
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static IReadOnlyList<string> GetReviewReasons(
        WpsGeneratedPdf pdf,
        PdfInspection inspection)
    {
        var reasons = new List<string>();
        if (pdf.WorksheetName is not null && inspection.PageCount > 1)
        {
            reasons.Add(
                $"工作表生成 {inspection.PageCount} 页，请确认是否存在尾行溢出或分页异常。");
        }

        if (inspection.HasMixedPageSizes)
        {
            reasons.Add("同一 PDF 内存在不同纸张尺寸，请核对页面设置。");
        }

        return reasons;
    }

    private static string CreateStableId(string relativePath)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(
                value.Select(character =>
                        invalidCharacters.Contains(character) ? '_' : character)
                    .ToArray())
            .Trim()
            .TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? "未命名.pdf" : sanitized;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static async Task WriteJsonAsync(
        string path,
        DraftBatch batch,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65536,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            batch,
            JsonOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void DeleteDirectoryInsideRoot(
        string root,
        string target)
    {
        if (!Directory.Exists(target))
        {
            return;
        }

        var fullRoot =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(
                fullRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"拒绝清理数据目录之外的路径：{fullTarget}");
        }

        Directory.Delete(fullTarget, true);
    }

    private sealed record ResolvedSource(
        string FullPath,
        string RelativePath,
        string Extension);
}
