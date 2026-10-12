using System.IO;
using System.Text.Json;
using CccPrint.Models;

namespace CccPrint.Services;

public sealed class SourceBaselineService
{
    private static readonly (string Id, string Name)[] Libraries =
    {
        ("ccc-gas", "CCC燃气资料"),
        ("electric-heating", "电加热资料"),
        ("government", "政府资料")
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _sourceRoot;
    private readonly string _baselinePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SourceBaselineService(string sourceRoot, string dataRoot)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _baselinePath = Path.Combine(dataRoot, "state", "source-baseline.json");
    }

    public async Task<SourceBaseline> EnsureInitializedAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return File.Exists(_baselinePath)
                ? await LoadCoreAsync(cancellationToken)
                : await ResetCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SourceBaseline> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ResetCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SourceBaseline> AcceptPublishedAsync(
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var baseline = File.Exists(_baselinePath)
                ? await LoadCoreAsync(cancellationToken)
                : await ResetCoreAsync(cancellationToken);
            var acceptedPaths = relativePaths
                .Select(NormalizeRelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentByPath = ScanCurrent().ToDictionary(
                item => item.RelativePath,
                StringComparer.OrdinalIgnoreCase);
            var updated = baseline.Files
                .Where(item => !acceptedPaths.Contains(item.RelativePath))
                .ToList();

            foreach (var path in acceptedPaths)
            {
                if (currentByPath.TryGetValue(path, out var snapshot))
                {
                    updated.Add(snapshot);
                }
            }

            var result = new SourceBaseline(
                baseline.SchemaVersion,
                DateTimeOffset.UtcNow,
                updated
                    .OrderBy(
                        item => item.RelativePath,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            await SaveCoreAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SourceChangeSet> GetChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var baseline = File.Exists(_baselinePath)
                ? await LoadCoreAsync(cancellationToken)
                : await ResetCoreAsync(cancellationToken);
            var current = ScanCurrent();

            var previousByPath = baseline.Files.ToDictionary(
                item => item.RelativePath,
                StringComparer.OrdinalIgnoreCase);
            var currentByPath = current.ToDictionary(
                item => item.RelativePath,
                StringComparer.OrdinalIgnoreCase);
            var changes = new List<SourceFileChange>();

            foreach (var item in current)
            {
                if (!previousByPath.TryGetValue(item.RelativePath, out var previous))
                {
                    changes.Add(ToChange("added", null, item));
                    continue;
                }

                if (previous.Length != item.Length ||
                    previous.LastWriteTimeUtc != item.LastWriteTimeUtc)
                {
                    changes.Add(ToChange("modified", previous, item));
                }
            }

            foreach (var previous in baseline.Files)
            {
                if (!currentByPath.ContainsKey(previous.RelativePath))
                {
                    changes.Add(ToChange("removed", previous, null));
                }
            }

            var ordered = changes
                .OrderBy(change => change.Kind, StringComparer.Ordinal)
                .ThenBy(
                    change => change.RelativePath,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new SourceChangeSet(
                DateTimeOffset.UtcNow,
                baseline.CreatedAtUtc,
                current.Count,
                ordered.Count(change => change.Kind == "added"),
                ordered.Count(change => change.Kind == "modified"),
                ordered.Count(change => change.Kind == "removed"),
                ordered);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SourceBaseline> ResetCoreAsync(
        CancellationToken cancellationToken)
    {
        var baseline = new SourceBaseline(
            1,
            DateTimeOffset.UtcNow,
            ScanCurrent());
        await SaveCoreAsync(baseline, cancellationToken);
        return baseline;
    }

    private async Task<SourceBaseline> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_baselinePath);
        return await JsonSerializer.DeserializeAsync<SourceBaseline>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? throw new InvalidDataException("源资料基线文件内容无效。");
    }

    private async Task SaveCoreAsync(
        SourceBaseline baseline,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_baselinePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_baselinePath)}.{Guid.NewGuid():N}.tmp");

        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         65536,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                baseline,
                JsonOptions,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _baselinePath, true);
    }

    private IReadOnlyList<SourceFileSnapshot> ScanCurrent()
    {
        if (!Directory.Exists(_sourceRoot))
        {
            throw new DirectoryNotFoundException(
                $"源资料目录不可用：{_sourceRoot}");
        }

        var files = new List<SourceFileSnapshot>();

        foreach (var library in Libraries)
        {
            var libraryRoot = Path.Combine(_sourceRoot, library.Name);
            if (!Directory.Exists(libraryRoot))
            {
                continue;
            }

            foreach (var filePath in EnumerateFilesSafely(libraryRoot))
            {
                try
                {
                    var file = new FileInfo(filePath);
                    if (file.Name.StartsWith("~$", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    files.Add(new SourceFileSnapshot(
                        NormalizeRelativePath(
                            Path.GetRelativePath(_sourceRoot, file.FullName)),
                        library.Id,
                        library.Name,
                        file.Extension.ToLowerInvariant(),
                        file.Length,
                        new DateTimeOffset(file.LastWriteTimeUtc)));
                }
                catch (IOException)
                {
                    // 文件保存过程中跳过，本次扫描不会阻断其他资料。
                }
                catch (UnauthorizedAccessException)
                {
                    // 不可读文件跳过。
                }
            }
        }

        return files
            .OrderBy(
                item => item.RelativePath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static SourceFileChange ToChange(
        string kind,
        SourceFileSnapshot? previous,
        SourceFileSnapshot? current)
    {
        var item = current ?? previous!;
        return new SourceFileChange(
            kind,
            item.RelativePath,
            item.LibraryId,
            item.LibraryName,
            item.Extension,
            previous?.Length,
            current?.Length,
            previous?.LastWriteTimeUtc,
            current?.LastWriteTimeUtc);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<string> EnumerateFilesSafely(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                pending.Push(subdirectory);
            }
        }
    }
}
