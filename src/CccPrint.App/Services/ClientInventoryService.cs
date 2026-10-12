using System.IO;
using System.Text.Json;
using CccPrint.Networking;

namespace CccPrint.Services;

public sealed class ClientInventoryService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClientInventoryService(string dataRoot)
    {
        _path = Path.Combine(
            Path.GetFullPath(dataRoot),
            "state",
            "client-inventory.json");
    }

    public async Task<ClientLibraryInventory?> GetLatestAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer
                .DeserializeAsync<ClientLibraryInventory>(
                    stream,
                    JsonOptions,
                    cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(
        ClientLibraryInventory inventory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(inventory.DeviceName) ||
            inventory.DeviceName.Length > 128 ||
            inventory.LocalManifest?.Files.Count > 5000)
        {
            throw new InvalidDataException("仓库端资料回报内容无效。");
        }

        var normalized = inventory with
        {
            ReportedAtUtc = DateTimeOffset.UtcNow
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
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
                    normalized,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
