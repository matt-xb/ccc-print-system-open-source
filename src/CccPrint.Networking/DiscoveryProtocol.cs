using System.Net;
using System.Text;
using System.Text.Json;

namespace CccPrint.Networking;

public static class DiscoveryProtocol
{
    public const string ProtocolName = "ccc-print-discovery-v1";
    public const string ProbeMessage = "CCC_PRINT_DISCOVER_V1";
    public const int DefaultDiscoveryPort = 8011;

    internal static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static byte[] CreateProbe()
    {
        return Encoding.UTF8.GetBytes(ProbeMessage);
    }

    public static bool IsProbe(ReadOnlySpan<byte> payload)
    {
        return payload.SequenceEqual(CreateProbe());
    }

    public static byte[] CreateAnnouncement(
        string deviceName,
        int servicePort,
        string appVersion)
    {
        return JsonSerializer.SerializeToUtf8Bytes(
            new DiscoveryAnnouncement(
                ProtocolName,
                deviceName,
                servicePort,
                appVersion),
            JsonOptions);
    }

    public static bool TryReadAnnouncement(
        ReadOnlySpan<byte> payload,
        out DiscoveryAnnouncement? announcement)
    {
        try
        {
            announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(
                payload,
                JsonOptions);
            return announcement is not null &&
                   string.Equals(
                       announcement.Protocol,
                       ProtocolName,
                       StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            announcement = null;
            return false;
        }
    }
}

public sealed record DiscoveryAnnouncement(
    string Protocol,
    string DeviceName,
    int ServicePort,
    string AppVersion);

public sealed record DiscoveredServer(
    string DeviceName,
    IPAddress Address,
    int ServicePort,
    string AppVersion)
{
    public Uri BaseAddress =>
        new UriBuilder(Uri.UriSchemeHttp, Address.ToString(), ServicePort).Uri;
}

