using System.Net.Http.Json;
using System.Security.Cryptography;

namespace CccPrint.Networking;

public sealed class LibrarySyncClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public LibrarySyncClient()
    {
        _httpClient = new HttpClient(
            new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5)
            })
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
    }

    public async Task<LibrarySyncManifest> GetManifestAsync(
        Uri serverBaseAddress,
        CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestCoreAsync(
            new Uri(
                serverBaseAddress,
                "api/v1/library/manifest"),
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(manifest.Version) ||
            manifest.Files.Count > 0)
        {
            return manifest;
        }

        return await GetManifestCoreAsync(
            new Uri(
                serverBaseAddress,
                "api/v1/library/test-manifest"),
            cancellationToken);
    }

    public async Task<LibrarySyncStatus> GetStatusAsync(
        Uri serverBaseAddress,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(3));
        var requestToken = timeoutSource.Token;
        using var response = await _httpClient.GetAsync(
            new Uri(serverBaseAddress, "api/v1/library/status"),
            requestToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            var manifest = await GetManifestAsync(
                serverBaseAddress,
                requestToken);
            return new LibrarySyncStatus(
                manifest.Version,
                manifest.IsTestData,
                manifest.GeneratedAtUtc,
                manifest.Files.Count,
                manifest.Notice);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content
                   .ReadFromJsonAsync<LibrarySyncStatus>(
                       DiscoveryProtocol.JsonOptions,
                       requestToken)
               ?? throw new InvalidDataException("资料状态内容无效。");
    }

    private async Task<LibrarySyncManifest> GetManifestCoreAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            uri,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content
                   .ReadFromJsonAsync<LibrarySyncManifest>(
                       DiscoveryProtocol.JsonOptions,
                       cancellationToken)
               ?? throw new InvalidDataException("资料更新清单内容无效。");
    }

    public async Task DownloadAndVerifyAsync(
        Uri serverBaseAddress,
        LibrarySyncFile file,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(serverBaseAddress, file.DownloadUrl);
        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[65536];
        long downloadedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            downloadedBytes += read;
            if (totalBytes is > 0)
            {
                progress?.Report(
                    Math.Min(1, downloadedBytes / (double)totalBytes.Value));
            }
        }

        await destination.FlushAsync(cancellationToken);
        destination.Close();

        var downloadedFile = new FileInfo(destinationPath);
        if (downloadedFile.Length != file.Bytes)
        {
            throw new InvalidDataException(
                $"资料文件大小不一致：{file.DisplayName}");
        }

        await using var verification = new FileStream(
            destinationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    verification,
                    cancellationToken))
            .ToLowerInvariant();
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"资料文件校验失败：{file.DisplayName}");
        }

        progress?.Report(1);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
