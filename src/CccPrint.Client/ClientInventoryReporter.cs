using System.Net.Http;
using System.Net.Http.Json;
using CccPrint.Networking;

namespace CccPrint.Client;

public sealed class ClientInventoryReporter : IDisposable
{
    private readonly HttpClient _client = new(
        new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public async Task ReportAsync(
        Uri serverBaseAddress,
        ClientLibraryInventory inventory,
        CancellationToken cancellationToken = default)
    {
        using var response = await _client.PostAsJsonAsync(
            new Uri(serverBaseAddress, "api/v1/client/inventory"),
            inventory,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}
