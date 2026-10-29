using System.Net;
using System.Net.Sockets;
using CSweet.Contracts.Plugins;
namespace CSweet.Infrastructure.Setup;
public sealed class ConnectionServiceNetworkOptions
{
    public const string Section = "CSweet:ConnectionServiceNetwork";
    public string[] AllowedOrigins { get; set; } = [ManagedServiceTrust.OfficialOrigin];
    public string[] PrivateNetworkOrigins { get; set; } = [];
}
public static class ConnectionServiceNetworkPolicy
{
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] >= 16 && b[1] <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 100 && b[1] >= 64 && b[1] <= 127 || b[0] == 198 && b[1] is 18 or 19);
        // Only global unicast IPv6, excluding transition/tunnel ranges.
        return b[0] >= 0x20 && b[0] <= 0x3f && !(b[0] == 0x20 && b[1] == 0x02) && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0);
    }
    public static SocketsHttpHandler Create(ConnectionServiceNetworkOptions options) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, ct) =>
        {
            var origin = ManagedServiceTrust.CanonicalOrigin(context.InitialRequestMessage.RequestUri!.GetLeftPart(UriPartial.Authority));
            if (!options.AllowedOrigins.Contains(origin, StringComparer.Ordinal)) throw new HttpRequestException("Connection service origin is not allowed by the server operator.");
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowPrivate = options.PrivateNetworkOrigins.Contains(origin, StringComparer.Ordinal);
            if (addresses.Length == 0 || !allowPrivate && addresses.Any(x => !IsPublic(x))) throw new HttpRequestException("Connection service resolved outside the operator network policy.");
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("Connection service is unavailable.");
        }
    };
}
