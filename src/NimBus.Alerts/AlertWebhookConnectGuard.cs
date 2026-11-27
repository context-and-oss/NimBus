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
    /// link-local, shared (CGNAT), benchmarking, multicast, reserved or unspecified. An IPv6
    /// address that carries an IPv4 address — IPv4-mapped (<c>::ffff:0:0/96</c>), NAT64
    /// well-known prefix (<c>64:ff9b::/96</c>), 6to4 (<c>2002::/16</c>) or IPv4-compatible
    /// (<c>::/96</c>, which includes <c>::</c> and <c>::1</c>) — is judged by that embedded IPv4
    /// address, because a translator or relay on the path delivers it there. The address is
    /// only classified, never rewritten.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        else if (TryGetEmbeddedIPv4(address, out var embedded))
        {
            address = embedded;
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

    /// <summary>
    /// Extracts the IPv4 address embedded in a NAT64 well-known-prefix, 6to4 or IPv4-compatible
    /// IPv6 address.
    /// </summary>
    private static bool TryGetEmbeddedIPv4(IPAddress address, out IPAddress embedded)
    {
        embedded = IPAddress.None;
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var b = address.GetAddressBytes();

        // 6to4, 2002::/16 (RFC 3056): the IPv4 address is bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            embedded = new IPAddress(b.AsSpan(2, 4));
            return true;
        }

        // NAT64 well-known prefix, 64:ff9b::/96 (RFC 6052), and IPv4-compatible, ::/96
        // (RFC 4291): the IPv4 address is the last 32 bits.
        var isNat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && IsZero(b.AsSpan(4, 8));
        var isCompatible = IsZero(b.AsSpan(0, 12));
        if (isNat64 || isCompatible)
        {
            embedded = new IPAddress(b.AsSpan(12, 4));
            return true;
        }

        return false;
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes) => !bytes.ContainsAnyExcept((byte)0);
}
