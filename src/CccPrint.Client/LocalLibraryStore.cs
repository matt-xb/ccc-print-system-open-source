using System.IO;
using System.Text.Json;
using CccPrint.Networking;

namespace CccPrint.Client;

public sealed class LocalLibraryStore
{
    private const int RetainedVersionCount = 2;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string _libraryRoot = Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
        CccPrint.Networking.Edition.DataDirectory,
        "打印端",
        "library");

    private string CurrentManifestPath =>
        Path.Combine(_libraryRoot, "current-manifest.json");

    public async Task<LibrarySyncManifest?> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(CurrentManifestPath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(CurrentManifestPath);
            return await JsonSerializer
                .DeserializeAsync<LibrarySyncManifest>(
                    stream,
                    JsonOptions,
                    cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            ClientLog.Write("读取本地资料清单失败", exception);
            return null;
        }
    }

    public string? GetPdfPath(
        LibrarySyncManifest manifest,
        LibrarySyncFile file)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return null;
        }

        var path = Path.Combine(
            GetVersionRoot(manifest.Version),
            "files",
            $"{file.FileId}.pdf");
        return File.Exists(path) ? path : null;
    }

    public bool IsComplete(LibrarySyncManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return false;
        }

        return IsVersionComplete(GetVersionRoot(manifest.Version), manifest);
    }

    public async Task SyncAsync(
        Uri serverBaseAddress,
        LibrarySyncManifest manifest,
        IProgress<LibrarySyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new InvalidDataException("资料清单没有版本号。");
        }

        Directory.CreateDirectory(_libraryRoot);
        var versionRoot = GetVersionRoot(manifest.Version);
        if (!IsVersionComplete(versionRoot, manifest))
        {
            var currentManifest = await LoadAsync(cancellationToken);
            var workingRoot = Path.Combine(
                _libraryRoot,
                $"working-{Guid.NewGuid():N}");
            var filesRoot = Path.Combine(workingRoot, "files");
            Directory.CreateDirectory(filesRoot);

            try
            {
                using var client = new LibrarySyncClient();
                for (var index = 0; index < manifest.Files.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = manifest.Files[index];
                    var destination = Path.Combine(
                        filesRoot,
                        $"{file.FileId}.pdf");
                    if (TryReuseLocalFile(
                            currentManifest,
                            file,
                            destination))
                    {
                        progress?.Report(
                            new LibrarySyncProgress(
                                index + 1,
                                manifest.Files.Count,
                                file.DisplayName,
                                1));
                        continue;
                    }

                    var fileProgress = new Progress<double>(value =>
                        progress?.Report(
                            new LibrarySyncProgress(
                                index,
                                manifest.Files.Count,
                                file.DisplayName,
                                value)));
                    await client.DownloadAndVerifyAsync(
                        serverBaseAddress,
                        file,
                        destination,
                        fileProgress,
                        cancellationToken);
                    progress?.Report(
                        new LibrarySyncProgress(
                            index + 1,
                            manifest.Files.Count,
                            file.DisplayName,
                            1));
                }

                Directory.CreateDirectory(
                    Path.GetDirectoryName(versionRoot)!);
                if (Directory.Exists(versionRoot))
                {
                    DeleteInsideLibraryRoot(versionRoot);
                }

                Directory.Move(workingRoot, versionRoot);
            }
            catch
            {
                DeleteInsideLibraryRoot(workingRoot);
                throw;
            }
        }

        await SaveCurrentManifestAsync(manifest, cancellationToken);
        PruneOldVersions(manifest.Version);
    }

    private bool TryReuseLocalFile(
        LibrarySyncManifest? currentManifest,
        LibrarySyncFile target,
        string destinationPath)
    {
        if (currentManifest is null)
        {
            return false;
        }

        var existing = currentManifest.Files.FirstOrDefault(file =>
            file.Bytes == target.Bytes &&
            file.Sha256.Equals(
                target.Sha256,
                StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return false;
        }

        var sourcePath = GetPdfPath(currentManifest, existing);
        if (sourcePath is null ||
            new FileInfo(sourcePath).Length != target.Bytes)
        {
            return false;
        }

        File.Copy(sourcePath, destinationPath, overwrite: false);
        return true;
    }

    private bool IsVersionComplete(
        string versionRoot,
        LibrarySyncManifest manifest)
    {
        if (!Directory.Exists(versionRoot))
        {
            return false;
        }

        return manifest.Files.All(file =>
        {
            var path = Path.Combine(
                versionRoot,
                "files",
                $"{file.FileId}.pdf");
            return File.Exists(path) &&
                   new FileInfo(path).Length == file.Bytes;
        });
    }

    private async Task SaveCurrentManifestAsync(
        LibrarySyncManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_libraryRoot);
        var temporaryPath = Path.Combine(
            _libraryRoot,
            $".current-manifest-{Guid.NewGuid():N}.tmp");
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
                manifest,
                JsonOptions,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, CurrentManifestPath, true);
    }

    private string GetVersionRoot(string version)
    {
        var safeVersion = string.Concat(
            version.Select(character =>
                char.IsLetterOrDigit(character) ||
                character is '-' or '_' or '.'
                    ? character
                    : '_'));
        return Path.Combine(_libraryRoot, "versions", safeVersion);
    }

    private void PruneOldVersions(string currentVersion)
    {
        var versionsRoot = Path.Combine(_libraryRoot, "versions");
        if (!Directory.Exists(versionsRoot))
        {
            return;
        }

        var currentRoot = GetVersionRoot(currentVersion);
        var retained = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(currentRoot)
        };
        var directories = Directory
            .EnumerateDirectories(versionsRoot)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToArray();
        foreach (var directory in directories)
        {
            if (retained.Count >= RetainedVersionCount)
            {
                break;
            }

            retained.Add(directory.FullName);
        }

        foreach (var directory in directories.Where(directory =>
                     !retained.Contains(directory.FullName)))
        {
            try
            {
                DeleteInsideLibraryRoot(directory.FullName);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                ClientLog.Write(
                    "清理旧资料版本失败",
                    exception);
            }
        }
    }

    private void DeleteInsideLibraryRoot(string target)
    {
        if (!Directory.Exists(target))
        {
            return;
        }

        var rootPrefix =
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_libraryRoot)) +
            Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(
                rootPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"拒绝清理资料目录之外的路径：{fullTarget}");
        }

        Directory.Delete(fullTarget, true);
    }
}
