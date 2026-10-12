using System.Net.Http.Json;
using System.Security.Cryptography;

namespace CccPrint.Networking;

public sealed class AppUpdateClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public AppUpdateClient()
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

    public async Task<AppUpdateManifest> GetManifestAsync(
        Uri serverBaseAddress,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(
            serverBaseAddress,
            "api/v1/app-update/manifest");
        using var response = await _httpClient.GetAsync(
            uri,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content
                   .ReadFromJsonAsync<AppUpdateManifest>(
                       DiscoveryProtocol.JsonOptions,
                       cancellationToken)
               ?? throw new InvalidDataException("软件更新清单内容无效。");
    }

    public async Task DownloadAndVerifyAsync(
        Uri serverBaseAddress,
        AppUpdateManifest manifest,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.DownloadUrl) ||
            string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            throw new InvalidDataException("软件更新清单缺少下载地址或校验值。");
        }

        var downloadUri = new Uri(serverBaseAddress, manifest.DownloadUrl);
        using var response = await _httpClient.GetAsync(
            downloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
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

        await using var verification = new FileStream(
            destinationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(
                    verification,
                    cancellationToken))
            .ToLowerInvariant();
        if (!actualHash.Equals(
                manifest.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "安装包校验失败，文件可能下载不完整，请重新尝试。");
        }

        progress?.Report(1);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
