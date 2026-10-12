using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CccPrint.Networking;

public sealed class ServerDiscoveryClient
{
    public async Task<IReadOnlyList<DiscoveredServer>> DiscoverAsync(
        string expectedDeviceName,
        int discoveryPort,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var udpClient = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };

        var probe = DiscoveryProtocol.CreateProbe();
        foreach (var address in GetProbeAddresses())
        {
            try
            {
                await udpClient.SendAsync(
                    probe,
                    new IPEndPoint(address, discoveryPort),
                    cancellationToken);
            }
            catch (SocketException)
            {
                // 某张虚拟网卡不支持广播时继续尝试其他网卡。
            }
        }

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var results = new Dictionary<string, DiscoveredServer>(
            StringComparer.OrdinalIgnoreCase);

        while (!timeoutSource.IsCancellationRequested)
        {
            UdpReceiveResult response;
            try
            {
                response = await udpClient.ReceiveAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // 某些网卡或安全软件会拒绝 UDP 接收，交给其他连接方式继续。
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!DiscoveryProtocol.TryReadAnnouncement(
                    response.Buffer,
                    out var announcement) ||
                announcement is null ||
                !string.Equals(
                    announcement.DeviceName,
                    expectedDeviceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var server = new DiscoveredServer(
                announcement.DeviceName,
                response.RemoteEndPoint.Address,
                announcement.ServicePort,
                announcement.AppVersion);
            results[$"{server.Address}:{server.ServicePort}"] = server;
        }

        return results.Values.ToArray();
    }

    private static IReadOnlyList<IPAddress> GetProbeAddresses()
    {
        var addresses = new HashSet<IPAddress>
        {
            IPAddress.Broadcast,
            IPAddress.Loopback
        };

        NetworkInterface[] networkInterfaces;
        try
        {
            networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return addresses.ToArray();
        }

        foreach (var networkInterface in networkInterfaces)
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType ==
                NetworkInterfaceType.Loopback)
            {
                continue;
            }

            UnicastIPAddressInformationCollection unicastAddresses;
            try
            {
                unicastAddresses = networkInterface
                    .GetIPProperties()
                    .UnicastAddresses;
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in unicastAddresses)
            {
                if (unicast.Address.AddressFamily !=
                        AddressFamily.InterNetwork ||
                    unicast.IPv4Mask is null)
                {
                    continue;
                }

                var addressBytes = unicast.Address.GetAddressBytes();
                var maskBytes = unicast.IPv4Mask.GetAddressBytes();
                var broadcastBytes = new byte[addressBytes.Length];
                for (var index = 0; index < addressBytes.Length; index++)
                {
                    broadcastBytes[index] =
                        (byte)(addressBytes[index] | ~maskBytes[index]);
                }

                addresses.Add(new IPAddress(broadcastBytes));
            }
        }

        return addresses.ToArray();
    }
}
