using System.IO;
using System.Security.Cryptography;
using CccPrint.Configuration;
using CccPrint.Networking;

namespace CccPrint.Services;

public sealed class AppUpdatePackageService
{
    private readonly AppOptions _options;
    private readonly string _packagePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedPackage? _cached;

    public AppUpdatePackageService(AppOptions options)
    {
        _options = options;
        _packagePath = Path.GetFullPath(
            Path.IsPathRooted(options.ClientUpdatePackagePath)
                ? options.ClientUpdatePackagePath
                : Path.Combine(
                    AppContext.BaseDirectory,
                    options.ClientUpdatePackagePath));
    }

    public string? GetAvailablePackagePath() =>
        File.Exists(_packagePath) ? _packagePath : null;

    public async Task<AppUpdateManifest> GetManifestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Version.TryParse(_options.ClientUpdateVersion, out _))
        {
            return Unavailable("管理端的软件更新版本配置无效。");
        }

        var file = new FileInfo(_packagePath);
        if (!file.Exists)
        {
            return Unavailable("管理端尚未放置打印端更新安装包。");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            file.Refresh();
            if (_cached is null ||
                _cached.Length != file.Length ||
                _cached.LastWriteTimeUtc != file.LastWriteTimeUtc)
            {
                await using var stream = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(
                        await SHA256.HashDataAsync(
                            stream,
                            cancellationToken))
                    .ToLowerInvariant();
                _cached = new CachedPackage(
                    file.Length,
                    file.LastWriteTimeUtc,
                    hash);
            }

            return new AppUpdateManifest(
                true,
                _options.ClientUpdateVersion,
                file.Name,
                "/api/v1/app-update/package",
                _cached.Length,
                _cached.Sha256,
                _options.ClientUpdateNotes,
                new DateTimeOffset(_cached.LastWriteTimeUtc),
                null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AppUpdateManifest Unavailable(string message) =>
        new(
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            message);

    private sealed record CachedPackage(
        long Length,
        DateTime LastWriteTimeUtc,
        string Sha256);
}
