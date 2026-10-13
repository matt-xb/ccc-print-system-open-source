using System.IO;
using System.Net;
using System.Text.Json;
using CccPrint.Networking;

namespace CccPrint.Client;

public sealed class ServerSettingsStore
{
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        CccPrint.Networking.Edition.DataDirectory,
        "打印端",
        "server.json");

    public async Task<IPAddress?> LoadAddressAsync(string deviceName)
    {
        if (!File.Exists(_settingsPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsPath);
            var settings = JsonSerializer.Deserialize<StoredServer>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            return settings is not null &&
                   string.Equals(
                       settings.DeviceName,
                       deviceName,
                       StringComparison.OrdinalIgnoreCase) &&
                   IPAddress.TryParse(settings.Address, out var address)
                ? address
                : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(DiscoveredServer server)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var settings = new StoredServer(
            server.DeviceName,
            server.Address.ToString(),
            server.ServicePort,
            DateTimeOffset.Now);
        await File.WriteAllTextAsync(
            _settingsPath,
            JsonSerializer.Serialize(
                settings,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
    }

    private sealed record StoredServer(
        string DeviceName,
        string Address,
        int Port,
        DateTimeOffset LastConnectedAt);
}
