using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Alerts;

/// <summary>
/// Keeps alert webhooks away from addresses inside the Resolver's own network. A subscription's
/// URL is chosen by an endpoint Owner, so without this the Resolver could be made to call
/// services only it can reach. The check runs on the address the connection is opened to, after
/// DNS resolution, so a public name that resolves to a private address is refused too.
/// </summary>
internal static class AlertWebhookConnectGuard
{
    /// <summary>
    /// A <see cref="SocketsHttpHandler.ConnectCallback"/> that connects to the first public
    /// address of the host and refuses a host that has none.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);

        SocketException? lastFailure = null;
        foreach (var address in addresses)
        {
            if (!IsPublic(address))
            {
                continue;
            }

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        if (lastFailure is not null)
        {
            throw lastFailure;
        }

        throw new AlertWebhookBlockedException(
            $"'{endpoint.Host}' has no public address. Alert webhooks connect to public addresses only unless {AlertWebhookOptions.SectionName}:AllowPrivateNetworks is set.");
    }

    /// <summary>
    /// Whether <paramref name="address"/> is a routable public address: not loopback, private,
    /// link-local, shared (CGNAT), benchmarking, multicast, reserved or unspecified.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => false,
                100 => (b[1] & 0xC0) != 64,                 // 100.64.0.0/10
                169 => b[1] != 254,                         // 169.254.0.0/16
                172 => (b[1] & 0xF0) != 16,                 // 172.16.0.0/12
                192 => b[1] != 168 && !(b[1] == 0 && b[2] == 0), // 192.168.0.0/16, 192.0.0.0/24
                198 => (b[1] & 0xFE) != 18,                 // 198.18.0.0/15
                >= 224 => false,                            // multicast and reserved
                _ => true,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !(IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.IPv6None)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6UniqueLocal
                || address.IsIPv6Multicast);
        }

        return false;
    }
}
