using System.IO;
using CccPrint.Models;

namespace CccPrint.Services;

public sealed class LibraryCatalogService
{
    private static readonly HashSet<string> PrintableCandidateExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc",
            ".docx",
            ".xls",
            ".xlsx",
            ".pdf"
        };

    private static readonly (string Id, string Name)[] Libraries =
    {
        ("ccc-gas", "CCC燃气资料"),
        ("electric-heating", "电加热资料"),
        ("government", "政府资料")
    };

    private readonly string _sourcePath;

    public LibraryCatalogService(string sourcePath)
    {
        _sourcePath = sourcePath;
    }

    public bool SourceAvailable => Directory.Exists(_sourcePath);

    public IReadOnlyList<LibrarySummary> Scan()
    {
        return Libraries
            .Select(library => ScanLibrary(library.Id, library.Name))
            .ToArray();
    }

    private LibrarySummary ScanLibrary(string id, string name)
    {
        var path = Path.Combine(_sourcePath, name);
        if (!Directory.Exists(path))
        {
            return new LibrarySummary(id, name, false, 0, 0, 0, null);
        }

        var sourceFileCount = 0;
        var printableCandidateCount = 0;
        var totalBytes = 0L;
        DateTimeOffset? latestWriteTime = null;

        foreach (var filePath in EnumerateFilesSafely(path))
        {
            try
            {
                var file = new FileInfo(filePath);
                if (file.Name.StartsWith("~$", StringComparison.Ordinal))
                {
                    continue;
                }

                sourceFileCount++;
                totalBytes += file.Length;

                if (PrintableCandidateExtensions.Contains(file.Extension))
                {
                    printableCandidateCount++;
                }

                var writeTime = new DateTimeOffset(file.LastWriteTimeUtc);
                if (latestWriteTime is null || writeTime > latestWriteTime)
                {
                    latestWriteTime = writeTime;
                }
            }
            catch (IOException)
            {
                // 文件正在被 Office 保存时，本轮扫描跳过该文件，下一次重新扫描。
            }
            catch (UnauthorizedAccessException)
            {
                // 不可读文件不会阻断其余资料统计。
            }
        }

        return new LibrarySummary(
            id,
            name,
            true,
            sourceFileCount,
            printableCandidateCount,
            totalBytes,
            latestWriteTime);
    }

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
