using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace CccPrint.Networking;

public sealed class ServerHealthClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public ServerHealthClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
    }

    public async Task<HealthResponse?> ProbeAsync(
        DiscoveredServer server,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var healthUri = new Uri(server.BaseAddress, "api/health");
            using var response = await _httpClient.GetAsync(
                healthUri,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<HealthResponse>(
                DiscoveryProtocol.JsonOptions,
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DiscoveredServer>> ResolveByDeviceNameAsync(
        string deviceName,
        int servicePort,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(
                deviceName,
                cancellationToken);
            return addresses
                .Where(address =>
                    address.AddressFamily == AddressFamily.InterNetwork)
                .Distinct()
                .Select(address =>
                    new DiscoveredServer(
                        deviceName,
                        address,
                        servicePort,
                        "unknown"))
                .ToArray();
        }
        catch (SocketException)
        {
            return Array.Empty<DiscoveredServer>();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<DiscoveredServer>();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public sealed record HealthResponse(
    string Status,
    string Service,
    string AppVersion,
    string Role,
    string HostName,
    int Port,
    bool SourceAvailable,
    int LibraryCount,
    DateTimeOffset CheckedAtUtc);
