using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using CccPrint.Models;
using CccPrint.Networking;

namespace CccPrint.Services;

public sealed class PublishedLibraryService
{
    internal const int RecentVersionRetentionCount = 30;
    internal const int MonthlyVersionRetentionCount = 12;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string _dataRoot;
    private readonly string _sourceRoot;
    private readonly string _draftsRoot;
    private readonly string _publishedRoot;
    private readonly string _versionsRoot;
    private readonly string _currentManifestPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PublishedLibraryService(string dataRoot, string sourceRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _draftsRoot = Path.Combine(_dataRoot, "drafts");
        _publishedRoot = Path.Combine(_dataRoot, "published");
        _versionsRoot = Path.Combine(_publishedRoot, "versions");
        _currentManifestPath = Path.Combine(
            _publishedRoot,
            "current-manifest.json");
    }

    public async Task<LibrarySyncManifest> GetCurrentManifestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_currentManifestPath))
        {
            return EmptyManifest();
        }

        try
        {
            await using var stream = File.OpenRead(_currentManifestPath);
            return await JsonSerializer
                       .DeserializeAsync<LibrarySyncManifest>(
                           stream,
                           JsonOptions,
                           cancellationToken)
                   ?? EmptyManifest();
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            return EmptyManifest(
                "正式资料清单暂时不可读取，请联系管理端重新发布。");
        }
    }

    public async Task<IReadOnlyList<LibrarySyncManifest>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_versionsRoot))
        {
            return Array.Empty<LibrarySyncManifest>();
        }

        var manifests = new List<LibrarySyncManifest>();
        foreach (var directory in Directory
                     .EnumerateDirectories(_versionsRoot)
                     .OrderByDescending(
                         Path.GetFileName,
                         StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = Path.GetFileName(directory);
            var manifest = await ReadVersionManifestAsync(
                version,
                cancellationToken);
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        return manifests
            .OrderByDescending(manifest => manifest.GeneratedAtUtc)
            .ToArray();
    }

    public string? GetVersionDirectory(string version)
    {
        if (!IsSafeSegment(version))
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(_versionsRoot, version));
        var rootPrefix =
            Path.TrimEndingDirectorySeparator(_versionsRoot) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(
                   rootPrefix,
                   StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(path)
            ? path
            : null;
    }

    public async Task<PublishResult> RestoreVersionAsync(
        string sourceVersion,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeSegment(sourceVersion))
        {
            throw new InvalidDataException("存档版本号无效。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var sourceManifest = await ReadVersionManifestAsync(
                                     sourceVersion,
                                     cancellationToken)
                                 ?? throw new FileNotFoundException(
                                     "找不到所选历史存档。");
            var current = await GetCurrentManifestAsync(cancellationToken);
            if (current.Version?.Equals(
                    sourceVersion,
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException(
                    "所选存档已经是当前正式版本。");
            }

            var version = CreateVersion();
            Directory.CreateDirectory(_versionsRoot);
            var workingRoot = Path.Combine(
                _publishedRoot,
                $".working-{Guid.NewGuid():N}");
            var finalRoot = Path.Combine(_versionsRoot, version);
            var filesRoot = Path.Combine(workingRoot, "files");
            Directory.CreateDirectory(filesRoot);

            try
            {
                var restoredFiles = new List<LibrarySyncFile>();
                foreach (var file in sourceManifest.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = ResolvePublishedPdf(
                        sourceManifest.Version,
                        file.FileId);
                    var destinationPath = Path.Combine(
                        filesRoot,
                        $"{file.FileId}.pdf");
                    await CopyAndVerifyAsync(
                        sourcePath,
                        destinationPath,
                        file.Bytes,
                        file.Sha256,
                        file.DisplayName,
                        cancellationToken);
                    restoredFiles.Add(file with
                    {
                        DownloadUrl =
                            $"/api/v1/library/files/{version}/{file.FileId}"
                    });
                }

                var publishedAt = DateTimeOffset.UtcNow;
                var manifest = new LibrarySyncManifest(
                    sourceManifest.SchemaVersion,
                    version,
                    false,
                    publishedAt,
                    $"恢复历史存档 {sourceVersion}：{sourceManifest.Notice}",
                    restoredFiles)
                {
                    RemovedSourcePaths = sourceManifest.RemovedSourcePaths
                };
                await WriteJsonAsync(
                    Path.Combine(workingRoot, "manifest.json"),
                    manifest,
                    cancellationToken);
                Directory.Move(workingRoot, finalRoot);
                await WriteJsonAtomicAsync(
                    _currentManifestPath,
                    manifest,
                    cancellationToken);
                await ApplyRetentionPolicyAsync(version);
                return new PublishResult(
                    version,
                    manifest.Files.Count,
                    publishedAt);
            }
            catch
            {
                DeleteInsideRoot(_publishedRoot, workingRoot);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PublishResult> PublishAsync(
        DraftBatch batch,
        DraftReviewState review,
        CancellationToken cancellationToken = default)
    {
        if (batch.Status.Equals(
                "test-only-do-not-publish",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "该批次是开发测试资料，禁止正式发布。请从已调整的源文件重新生成草稿。");
        }

        if (batch.FailedSourceCount > 0)
        {
            throw new InvalidOperationException(
                "批次中存在转换失败的原件，不能正式发布。");
        }

        if (string.IsNullOrWhiteSpace(review.ReleaseNote))
        {
            throw new InvalidOperationException("请先填写本次更新说明。");
        }

        var excludedOutputIds = review.Items
            .Where(item => item.Decision == ReviewDecisions.Rejected)
            .Select(item => item.OutputId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputs = batch.Sources
            .Where(source => source.Success)
            .SelectMany(source => source.Outputs.Select(output =>
                new OutputWithSource(source, output)))
            .Where(item => !excludedOutputIds.Contains(item.Output.OutputId))
            .ToArray();
        if (outputs.Length == 0 && batch.RemovedSourcePaths.Count == 0)
        {
            throw new InvalidOperationException(
                "本次没有可发布的 PDF 或待删除资料。");
        }

        foreach (var source in batch.Sources.Where(item => item.Success))
        {
            await VerifySourceUnchangedAsync(source, cancellationToken);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previousManifest =
                await GetCurrentManifestAsync(cancellationToken);
            var version = CreateVersion();
            Directory.CreateDirectory(_versionsRoot);
            var workingRoot = Path.Combine(
                _publishedRoot,
                $".working-{Guid.NewGuid():N}");
            var finalRoot = Path.Combine(_versionsRoot, version);
            var filesRoot = Path.Combine(workingRoot, "files");
            Directory.CreateDirectory(filesRoot);

            try
            {
                var manifestFiles = new List<LibrarySyncFile>();
                var removedSourceSet = batch.RemovedSourcePaths
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var fullyReplacedSources = batch.Sources
                    .Where(source =>
                        source.Success &&
                        source.Outputs.Count > 0 &&
                        source.Outputs.All(output =>
                            !excludedOutputIds.Contains(output.OutputId)))
                    .Select(source => source.RelativePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selectedOutputsBySource = outputs
                    .GroupBy(
                        item => item.Source.RelativePath,
                        StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.Select(item => item.Output).ToArray(),
                        StringComparer.OrdinalIgnoreCase);
                foreach (var previousFile in previousManifest.Files.Where(
                             file =>
                                 !removedSourceSet.Contains(file.SourceRelativePath) &&
                                 !fullyReplacedSources.Contains(file.SourceRelativePath) &&
                                 (!selectedOutputsBySource.TryGetValue(
                                      file.SourceRelativePath,
                                      out var selected) ||
                                  !selected.Any(output =>
                                      IsSameOutput(file, output)))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = ResolvePublishedPdf(
                        previousManifest.Version,
                        previousFile.FileId);
                    var destinationPath = Path.Combine(
                        filesRoot,
                        $"{previousFile.FileId}.pdf");
                    await CopyAndVerifyAsync(
                        sourcePath,
                        destinationPath,
                        previousFile.Bytes,
                        previousFile.Sha256,
                        previousFile.DisplayName,
                        cancellationToken);
                    manifestFiles.Add(previousFile with
                    {
                        DownloadUrl =
                            $"/api/v1/library/files/{version}/" +
                            previousFile.FileId
                    });
                }

                foreach (var item in outputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = ResolveDraftPdf(item.Output);
                    var fileId = CreateUniqueFileId(
                        item.Output.OutputId,
                        item.Output.Sha256,
                        manifestFiles);
                    var destinationPath = Path.Combine(
                        filesRoot,
                        $"{fileId}.pdf");
                    await CopyAndVerifyAsync(
                        sourcePath,
                        destinationPath,
                        item.Output,
                        cancellationToken);
                    var (libraryId, libraryName) =
                        GetLibrary(item.Source.RelativePath);
                    manifestFiles.Add(new LibrarySyncFile(
                        fileId,
                        libraryId,
                        libraryName,
                        item.Source.RelativePath,
                        item.Output.DisplayName,
                        item.Output.WorksheetName,
                        item.Output.WorksheetIndex,
                        item.Output.PageCount,
                        item.Output.Bytes,
                        item.Output.Sha256,
                        false,
                        $"/api/v1/library/files/{version}/" +
                        fileId));
                }

                var publishedAt = DateTimeOffset.UtcNow;
                var replacedSources = outputs
                    .Select(item => item.Source.RelativePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var removedSources = previousManifest.RemovedSourcePaths
                    .Where(path => !replacedSources.Contains(path))
                    .Concat(batch.RemovedSourcePaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var manifest = new LibrarySyncManifest(
                    1,
                    version,
                    false,
                    publishedAt,
                    review.ReleaseNote.Trim(),
                    manifestFiles
                        .OrderBy(
                            file => file.LibraryName,
                            StringComparer.Ordinal)
                        .ThenBy(
                            file => file.DisplayName,
                            StringComparer.OrdinalIgnoreCase)
                        .ToArray())
                {
                    RemovedSourcePaths = removedSources
                };
                await WriteJsonAsync(
                    Path.Combine(workingRoot, "manifest.json"),
                    manifest,
                    cancellationToken);
                Directory.Move(workingRoot, finalRoot);
                await WriteJsonAtomicAsync(
                    _currentManifestPath,
                    manifest,
                    cancellationToken);
                await MarkBatchPublishedAsync(batch, cancellationToken);
                await ApplyRetentionPolicyAsync(version);
                return new PublishResult(
                    version,
                    manifest.Files.Count,
                    publishedAt);
            }
            catch
            {
                DeleteInsideRoot(_publishedRoot, workingRoot);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetFilePathAsync(
        string version,
        string fileId,
        CancellationToken cancellationToken = default)
    {
        if (Path.GetFileName(version) != version ||
            Path.GetFileName(fileId) != fileId)
        {
            return null;
        }

        var manifestPath = Path.Combine(
            _versionsRoot,
            version,
            "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        LibrarySyncManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer
                .DeserializeAsync<LibrarySyncManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            return null;
        }

        if (manifest?.Files.Any(file =>
                file.FileId.Equals(
                    fileId,
                    StringComparison.OrdinalIgnoreCase)) != true)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(
            _versionsRoot,
            version,
            "files",
            $"{fileId}.pdf"));
        var rootPrefix =
            Path.TrimEndingDirectorySeparator(_versionsRoot) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(
                   rootPrefix,
                   StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path)
            ? path
            : null;
    }

    private string ResolveDraftPdf(PdfDraftOutput output)
    {
        var path = Path.GetFullPath(Path.Combine(
            _dataRoot,
            output.RelativePdfPath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        var rootPrefix =
            Path.TrimEndingDirectorySeparator(_draftsRoot) +
            Path.DirectorySeparatorChar;
        if (!path.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
        {
            throw new FileNotFoundException(
                $"找不到待发布 PDF：{output.DisplayName}",
                path);
        }

        return path;
    }

    private async Task<LibrarySyncManifest?> ReadVersionManifestAsync(
        string version,
        CancellationToken cancellationToken)
    {
        if (!IsSafeSegment(version))
        {
            return null;
        }

        var manifestPath = Path.Combine(
            _versionsRoot,
            version,
            "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer
                .DeserializeAsync<LibrarySyncManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken);
            return manifest?.Version?.Equals(
                       version,
                       StringComparison.OrdinalIgnoreCase) == true
                ? manifest
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            return null;
        }
    }

    private string ResolvePublishedPdf(
        string? version,
        string fileId)
    {
        if (string.IsNullOrWhiteSpace(version) ||
            Path.GetFileName(version) != version ||
            Path.GetFileName(fileId) != fileId)
        {
            throw new InvalidDataException(
                "上一正式版本的文件路径无效。");
        }

        var path = Path.GetFullPath(Path.Combine(
            _versionsRoot,
            version,
            "files",
            $"{fileId}.pdf"));
        var rootPrefix =
            Path.TrimEndingDirectorySeparator(_versionsRoot) +
            Path.DirectorySeparatorChar;
        if (!path.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
        {
            throw new FileNotFoundException(
                "上一正式版本缺少需要沿用的 PDF。",
                path);
        }

        return path;
    }

    private async Task VerifySourceUnchangedAsync(
        DraftSourceItem source,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            _sourceRoot,
            source.RelativePath.Replace(
                '/',
                Path.DirectorySeparatorChar)));
        var rootPrefix =
            Path.TrimEndingDirectorySeparator(_sourceRoot) +
            Path.DirectorySeparatorChar;
        if (!sourcePath.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sourcePath))
        {
            throw new InvalidOperationException(
                $"源文件已移动或删除，请重新生成草稿：{source.RelativePath}");
        }

        await using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!hash.Equals(
                source.SourceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"源文件在制稿后发生了变化，请重新生成并审阅：{source.RelativePath}");
        }
    }

    private async Task MarkBatchPublishedAsync(
        DraftBatch batch,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(
            _draftsRoot,
            batch.BatchId,
            "batch.json");
        await WriteJsonAtomicAsync(
            manifestPath,
            batch with { Status = "published" },
            cancellationToken);
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        PdfDraftOutput output,
        CancellationToken cancellationToken)
    {
        await CopyAndVerifyAsync(
            sourcePath,
            destinationPath,
            output.Bytes,
            output.Sha256,
            output.DisplayName,
            cancellationToken);
    }

    private static async Task CopyAndVerifyAsync(
        string sourcePath,
        string destinationPath,
        long expectedBytes,
        string expectedSha256,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using (var source = new FileStream(
                         sourcePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         65536,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        await using (var destination = new FileStream(
                         destinationPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         65536,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }

        var file = new FileInfo(destinationPath);
        if (file.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"发布文件大小不一致：{displayName}");
        }

        await using var verification = File.OpenRead(destinationPath);
        var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    verification,
                    cancellationToken))
            .ToLowerInvariant();
        if (!hash.Equals(
                expectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"发布文件校验失败：{displayName}");
        }
    }

    private static (string Id, string Name) GetLibrary(string relativePath)
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

    private static bool IsSameOutput(
        LibrarySyncFile previous,
        PdfDraftOutput current)
    {
        if (previous.FileId.Equals(
                current.OutputId,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(previous.WorksheetName) &&
               !string.IsNullOrWhiteSpace(current.WorksheetName) &&
               previous.WorksheetName.Equals(
                   current.WorksheetName,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateUniqueFileId(
        string preferredId,
        string sha256,
        IReadOnlyCollection<LibrarySyncFile> files)
    {
        if (!files.Any(file => file.FileId.Equals(
                preferredId,
                StringComparison.OrdinalIgnoreCase)))
        {
            return preferredId;
        }

        var suffix = sha256[..Math.Min(8, sha256.Length)];
        var candidate = $"{preferredId}-{suffix}";
        var counter = 2;
        while (files.Any(file => file.FileId.Equals(
                   candidate,
                   StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{preferredId}-{suffix}-{counter++}";
        }

        return candidate;
    }

    internal async Task<int> ApplyRetentionPolicyAsync(
        string currentVersion)
    {
        if (!Directory.Exists(_versionsRoot))
        {
            return 0;
        }

        var versions = new List<ArchivedVersion>();
        foreach (var directory in Directory.EnumerateDirectories(_versionsRoot))
        {
            var version = Path.GetFileName(directory);
            if (!IsSafeSegment(version))
            {
                continue;
            }

            var manifest = await ReadVersionManifestAsync(
                version,
                CancellationToken.None);
            if (manifest is not null)
            {
                versions.Add(new ArchivedVersion(
                    version,
                    directory,
                    manifest.GeneratedAtUtc));
            }
        }

        var ordered = versions
            .OrderByDescending(item => item.GeneratedAtUtc)
            .ThenByDescending(
                item => item.Version,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var retained = ordered
            .Take(RecentVersionRetentionCount)
            .Select(item => item.Version)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        retained.Add(currentVersion);

        foreach (var monthlyVersion in ordered
                     .GroupBy(item => new
                     {
                         item.GeneratedAtUtc.Year,
                         item.GeneratedAtUtc.Month
                     })
                     .OrderByDescending(group => group.Key.Year)
                     .ThenByDescending(group => group.Key.Month)
                     .Take(MonthlyVersionRetentionCount)
                     .Select(group => group.First()))
        {
            retained.Add(monthlyVersion.Version);
        }

        var deleted = 0;
        foreach (var obsolete in ordered.Where(item =>
                     !retained.Contains(item.Version)))
        {
            try
            {
                DeleteInsideRoot(_versionsRoot, obsolete.Directory);
                deleted++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning(
                    $"清理历史存档失败：{obsolete.Version}；{exception.Message}");
            }
        }

        return deleted;
    }

    private string CreateVersion()
    {
        var prefix = DateTimeOffset.Now.ToString("yyyy.MM.dd-HHmmss");
        var version = prefix;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(_versionsRoot, version)))
        {
            version = $"{prefix}-{suffix++}";
        }

        return version;
    }

    private static bool IsSafeSegment(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Path.GetFileName(value) == value;

    private static LibrarySyncManifest EmptyManifest(
        string notice = "当前还没有正式发布的资料版本。") =>
        new(
            1,
            null,
            false,
            DateTimeOffset.UtcNow,
            notice,
            Array.Empty<LibrarySyncFile>());

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
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
            value,
            JsonOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        await WriteJsonAsync(temporaryPath, value, cancellationToken);
        File.Move(temporaryPath, path, true);
    }

    private static void DeleteInsideRoot(string root, string target)
    {
        if (!Directory.Exists(target))
        {
            return;
        }

        var rootPrefix =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"拒绝清理发布目录之外的路径：{fullTarget}");
        }

        Directory.Delete(fullTarget, true);
    }

    private sealed record OutputWithSource(
        DraftSourceItem Source,
        PdfDraftOutput Output);

    private sealed record ArchivedVersion(
        string Version,
        string Directory,
        DateTimeOffset GeneratedAtUtc);
}
