using System.Net;
using System.Net.Sockets;

namespace Tripo.Mcp;

internal static class PublicNetworkConnector
{
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(
                    context.DnsEndPoint.Host,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException exception)
        {
            throw new HttpRequestException(
                "The signed download host could not be resolved.",
                exception);
        }

        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException(
                "The signed download host resolved to a non-public address.");
        }

        List<Exception> failures = [];
        foreach (IPAddress address in addresses)
        {
            Socket socket = new(
                address.AddressFamily,
                SocketType.Stream,
                ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                        new IPEndPoint(address, context.DnsEndPoint.Port),
                        cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (
                exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                failures.Add(exception);
                if (exception is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException(
            "No public address for the signed download host accepted a connection.",
            new AggregateException(failures));
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] != 0 &&
                   bytes[0] != 10 &&
                   bytes[0] != 127 &&
                   !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                   !(bytes[0] == 169 && bytes[1] == 254) &&
                   !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                   !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) &&
                   !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) &&
                   !(bytes[0] == 192 && bytes[1] == 168) &&
                   !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
                   !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
                   !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) &&
                   bytes[0] < 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            bool documentationPrefix =
                bytes[0] == 0x20 &&
                bytes[1] == 0x01 &&
                bytes[2] == 0x0d &&
                bytes[3] == 0xb8;

            // 6to4 (2002::/16) and Teredo (2001:0000::/32) tunnel to an arbitrary
            // embedded IPv4 address, so they must be rejected like the IPv4 ranges.
            bool sixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
            bool teredo =
                bytes[0] == 0x20 &&
                bytes[1] == 0x01 &&
                bytes[2] == 0x00 &&
                bytes[3] == 0x00;
            return !address.Equals(IPAddress.IPv6Any) &&
                   !address.Equals(IPAddress.IPv6None) &&
                   !address.Equals(IPAddress.IPv6Loopback) &&
                   !address.IsIPv6LinkLocal &&
                   !address.IsIPv6Multicast &&
                   !address.IsIPv6SiteLocal &&
                   (bytes[0] & 0xfe) != 0xfc &&
                   !documentationPrefix &&
                   !sixToFour &&
                   !teredo;
        }

        return false;
    }
}
