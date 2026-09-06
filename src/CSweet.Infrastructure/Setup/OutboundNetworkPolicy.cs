using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CSweet.Infrastructure.Setup;

public static class OutboundNetworkPolicy
{
    public static string NormalizeHost(string host)
    {
        var value = host.Trim().TrimEnd('.');
        return new IdnMapping().GetAscii(value).ToLowerInvariant();
    }

    public static string NormalizeOrigin(Uri uri)
    {
        var host = NormalizeHost(uri.DnsSafeHost);
        return $"{uri.Scheme.ToLowerInvariant()}://{host}:{uri.Port}";
    }

    public static bool IsAllowedOrigin(Uri uri, IEnumerable<string> allowedOrigins)
    {
        var origin = NormalizeOrigin(uri);
        foreach (var configured in allowedOrigins)
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var allowed))
                continue;
            if (string.Equals(origin, NormalizeOrigin(allowed), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsPathWithinPrefix(string path, string prefix)
    {
        var normalizedPrefix = string.IsNullOrWhiteSpace(prefix) ? "/" : prefix;
        if (!normalizedPrefix.StartsWith('/'))
            normalizedPrefix = "/" + normalizedPrefix;
        if (normalizedPrefix == "/")
            return true;
        normalizedPrefix = normalizedPrefix.TrimEnd('/');
        return string.Equals(path, normalizedPrefix, StringComparison.Ordinal) ||
               path.StartsWith(normalizedPrefix + "/", StringComparison.Ordinal);
    }

    public static IReadOnlyList<CidrRange> ParseCidrs(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return [];
        var ranges = new List<CidrRange>();
        foreach (var value in configured.Split([',', ';', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (CidrRange.TryParse(value, out var range))
                ranges.Add(range);
        return ranges;
    }

    public static bool IsForbiddenAddress(IPAddress address, IReadOnlyList<CidrRange>? blockedCidrs = null)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (blockedCidrs?.Any(x => x.Contains(address)) == true)
            return true;
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] is 0 or 10 or 127 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                   bytes[0] >= 224;

        return address.IsIPv6LinkLocal ||
               address.IsIPv6Multicast ||
               address.Equals(IPAddress.IPv6Loopback) ||
               (bytes[0] & 0xfe) == 0xfc;
    }

    public readonly record struct CidrRange(byte[] Network, int PrefixLength)
    {
        public static bool TryParse(string value, out CidrRange range)
        {
            range = default;
            var parts = value.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var address))
                return false;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            var maxBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = maxBits;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maxBits))
                return false;
            range = new CidrRange(address.GetAddressBytes(), prefix);
            return true;
        }

        public bool Contains(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            var candidate = address.GetAddressBytes();
            if (candidate.Length != Network.Length)
                return false;
            var wholeBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;
            for (var index = 0; index < wholeBytes; index++)
                if (candidate[index] != Network[index])
                    return false;
            if (remainingBits == 0)
                return true;
            var mask = (byte)(0xff << (8 - remainingBits));
            return (candidate[wholeBytes] & mask) == (Network[wholeBytes] & mask);
        }
    }
}
