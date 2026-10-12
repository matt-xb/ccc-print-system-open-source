using System.Net;
using System.Net.Sockets;

namespace CccPrint.Networking;

public sealed class DiscoveryResponder : IAsyncDisposable
{
    private readonly int _discoveryPort;
    private readonly string _deviceName;
    private readonly int _servicePort;
    private readonly string _appVersion;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _stopSource;
    private Task? _listenTask;

    public DiscoveryResponder(
        int discoveryPort,
        string deviceName,
        int servicePort,
        string appVersion)
    {
        _discoveryPort = discoveryPort;
        _deviceName = deviceName;
        _servicePort = servicePort;
        _appVersion = appVersion;
    }

    public Task StartAsync()
    {
        if (_udpClient is not null)
        {
            return Task.CompletedTask;
        }

        _udpClient = new UdpClient(
            new IPEndPoint(IPAddress.Any, _discoveryPort));
        _stopSource = new CancellationTokenSource();
        _listenTask = ListenAsync(_udpClient, _stopSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_udpClient is null)
        {
            return;
        }

        _stopSource?.Cancel();
        _udpClient.Dispose();

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask;
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
            catch (SocketException)
            {
                // Dispose 会中断正在等待的 UDP 接收。
            }
        }

        _stopSource?.Dispose();
        _stopSource = null;
        _listenTask = null;
        _udpClient = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task ListenAsync(
        UdpClient udpClient,
        CancellationToken cancellationToken)
    {
        var announcement = DiscoveryProtocol.CreateAnnouncement(
            _deviceName,
            _servicePort,
            _appVersion);

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult request;
            try
            {
                request = await udpClient.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!DiscoveryProtocol.IsProbe(request.Buffer))
            {
                continue;
            }

            try
            {
                await udpClient.SendAsync(
                    announcement,
                    request.RemoteEndPoint,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // 单个客户端离线不影响后续发现请求。
            }
        }
    }
}

