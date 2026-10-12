using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CccPrint.Networking;

public sealed class ServerLocator : IDisposable
{
    private readonly ServerDiscoveryClient _discoveryClient = new();
    private readonly ServerHealthClient _healthClient = new();

    public async Task<ServerConnectionResult> LocateAsync(
        string expectedDeviceName,
        int servicePort,
        int discoveryPort,
        IPAddress? cachedAddress,
        CancellationToken cancellationToken = default)
    {
        if (cachedAddress is not null && IsUsableLanAddress(cachedAddress))
        {
            var cachedServer = new DiscoveredServer(
                expectedDeviceName,
                cachedAddress,
                servicePort,
                "cached");
            var cachedResult = await VerifyAsync(
                cachedServer,
                expectedDeviceName,
                "缓存地址",
                cancellationToken);
            if (cachedResult is not null)
            {
                return cachedResult;
            }
        }

        var resolved = await _healthClient.ResolveByDeviceNameAsync(
            expectedDeviceName,
            servicePort,
            cancellationToken);
        foreach (var server in resolved)
        {
            var result = await VerifyAsync(
                server,
                expectedDeviceName,
                "设备名称解析",
                cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        IReadOnlyList<DiscoveredServer> discovered;
        try
        {
            discovered = await _discoveryClient.DiscoverAsync(
                expectedDeviceName,
                discoveryPort,
                TimeSpan.FromSeconds(3),
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is SocketException or
                NetworkInformationException or
                ObjectDisposedException)
        {
            discovered = Array.Empty<DiscoveredServer>();
        }

        foreach (var server in discovered
                     .OrderByDescending(item => GetAddressPriority(item.Address)))
        {
            var result = await VerifyAsync(
                server,
                expectedDeviceName,
                "局域网自动发现",
                cancellationToken);
            if (result is not null)
            {
                return result;
            }
        }

        return new ServerConnectionResult(
            false,
            null,
            null,
            "未找到管理端。请确认管理端已运行，并且两台电脑位于可互访的公司局域网。");
    }

    public void Dispose()
    {
        _healthClient.Dispose();
    }

    private async Task<ServerConnectionResult?> VerifyAsync(
        DiscoveredServer server,
        string expectedDeviceName,
        string method,
        CancellationToken cancellationToken)
    {
        var health = await _healthClient.ProbeAsync(
            server,
            cancellationToken);
        if (health is null ||
            !string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                health.HostName,
                expectedDeviceName,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ServerConnectionResult(
            true,
            server with
            {
                DeviceName = health.HostName,
                ServicePort = health.Port,
                AppVersion = health.AppVersion
            },
            health,
            $"已通过{method}连接管理端。");
    }

    private static int GetAddressPriority(IPAddress address)
    {
        if (!IsUsableLanAddress(address))
        {
            return 0;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => 3,
            172 when bytes[1] is >= 16 and <= 31 => 3,
            192 when bytes[1] == 168 => 3,
            _ => 1
        };
    }

    private static bool IsUsableLanAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               !(bytes[0] == 169 && bytes[1] == 254) &&
               !(bytes[0] == 198 && bytes[1] is 18 or 19);
    }
}

public sealed record ServerConnectionResult(
    bool Success,
    DiscoveredServer? Server,
    HealthResponse? Health,
    string Message);
