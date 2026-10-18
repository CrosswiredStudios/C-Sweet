using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CSweet.Compute.Runtime;

/// <summary>Checks socket destinations, never guest-supplied claims about DNS or routing.</summary>
internal sealed class ComputePublicInternetPolicy
{
    private static readonly IPNetwork[] Reserved = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "192.88.99.0/24",
        "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
        "168.63.129.16/32", "2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20"
    }.Select(IPNetwork.Parse).ToArray();
    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private readonly IPNetwork[] _blocked;
    private readonly bool _allowIpv6;

    public ComputePublicInternetPolicy(IEnumerable<string>? blockedCidrs = null, bool allowIpv6 = true)
    {
        _allowIpv6 = allowIpv6;
        _blocked = (blockedCidrs ?? []).Select(value =>
        {
            var range = IPNetwork.Parse(value);
            return range.BaseAddress.IsIPv4MappedToIPv6 && range.PrefixLength >= 96
                ? new IPNetwork(range.BaseAddress.MapToIPv4(), range.PrefixLength - 96) : range;
        }).ToArray();
    }

    public bool IsAllowed(IPAddress address, IEnumerable<IPAddress>? hostAddresses = null)
        => (_allowIpv6 || address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6) &&
            IsAddressAllowed(address, hostAddresses);

    private bool IsAddressAllowed(IPAddress address, IEnumerable<IPAddress>? hostAddresses)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || Reserved.Any(x => x.Contains(address)) ||
            _blocked.Any(x => x.Contains(address) ||
                address.AddressFamily == AddressFamily.InterNetwork && ContainsMappedAddress(x, address)) ||
            address.AddressFamily == AddressFamily.InterNetworkV6 && !GlobalV6.Contains(address)) return false;
        return !(hostAddresses ?? LocalAddresses()).Any(x =>
            (x.IsIPv4MappedToIPv6 ? x.MapToIPv4() : x).Equals(address));
    }

    private static bool ContainsMappedAddress(IPNetwork range, IPAddress address)
    {
        if (range.BaseAddress.AddressFamily != AddressFamily.InterNetworkV6) return false;
        // IPNetwork.Contains normalizes mapped addresses to IPv4, so compare the IPv6
        // prefix explicitly when applying a broad administrator-supplied IPv6 block.
        var candidate = address.MapToIPv6().GetAddressBytes();
        var network = range.BaseAddress.GetAddressBytes();
        var whole = range.PrefixLength / 8;
        var remainder = range.PrefixLength % 8;
        return candidate.AsSpan(0, whole).SequenceEqual(network.AsSpan(0, whole)) &&
            (remainder == 0 || (candidate[whole] & (255 << (8 - remainder))) == network[whole]);
    }

    public async Task<IPAddress> ResolveAsync(string address, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 253 || address.Contains('%'))
            throw new UnauthorizedAccessException("network_destination_denied");
        var addresses = IPAddress.TryParse(address, out var literal) ? [literal] :
            await Dns.GetHostAddressesAsync(address, token);
        var hosts = LocalAddresses().ToArray();
        // Mixed public/private answers are rejected rather than silently selecting a convenient answer.
        if (addresses.Length == 0 || addresses.Any(x => !IsAddressAllowed(x, hosts)))
            throw new UnauthorizedAccessException("network_destination_denied");
        return addresses.FirstOrDefault(x => _allowIpv6 || x.AddressFamily == AddressFamily.InterNetwork || x.IsIPv4MappedToIPv6)
            ?? throw new UnauthorizedAccessException("network_destination_denied");
    }

    private static IEnumerable<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(x => x.GetIPProperties().UnicastAddresses).Select(x => x.Address);
}
