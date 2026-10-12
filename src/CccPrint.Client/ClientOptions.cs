using System.IO;
using System.Text.Json;

namespace CccPrint.Client;

public sealed class ClientOptions
{
    public string ServerDeviceName { get; init; } = "YOUR-ADMIN-PC";

    public int ServerPort { get; init; } = 8010;

    public int DiscoveryPort { get; init; } = 8011;

    public static ClientOptions Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            return new ClientOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<ClientOptions>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions
                       {
                           PropertyNameCaseInsensitive = true
                       })
                   ?? new ClientOptions();
        }
        catch
        {
            return new ClientOptions();
        }
    }
}

