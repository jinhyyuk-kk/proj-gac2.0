using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Gac.Server.Hosting;

/// <summary>
/// Auto-detects the school LAN range during server setup (CHANGE-001 §2): the adapter that has a default gateway, its
/// IPv4 address and prefix length, computed as a CIDR network (e.g. 10.20.0.0/22). Prefers wired Ethernet, then the
/// lowest interface metric. This range only controls who may reach the port — never desktop-vs-laptop (that is §1.2).
/// </summary>
public static class LanDetection
{
    public static string? DetectCidr()
    {
        var candidates =
            from n in NetworkInterface.GetAllNetworkInterfaces()
            where n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
            let p = n.GetIPProperties()
            where p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
            from u in p.UnicastAddresses
            where u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is > 0 and <= 32 && !IsApipa(u.Address)
            select new { n, u.Address, u.PrefixLength, Wired = n.NetworkInterfaceType == NetworkInterfaceType.Ethernet, Metric = Metric(p) };

        var best = candidates
            .OrderByDescending(c => c.Wired)
            .ThenBy(c => c.Metric)
            .FirstOrDefault();
        return best == null ? null : ToCidr(best.Address, best.PrefixLength);
    }

    private static bool IsApipa(IPAddress a) => a.GetAddressBytes() is [169, 254, ..];

    private static int Metric(IPInterfaceProperties p)
    {
        try { return p.GetIPv4Properties()?.Index ?? int.MaxValue; } catch (NetworkInformationException) { return int.MaxValue; }
    }

    /// <summary>Network address / prefix, e.g. 10.20.3.7 /22 → "10.20.0.0/22".</summary>
    public static string ToCidr(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var mask = PrefixToMask(prefixLength);
        for (var i = 0; i < 4; i++) bytes[i] &= mask[i];
        return $"{new IPAddress(bytes)}/{prefixLength}";
    }

    private static byte[] PrefixToMask(int prefix)
    {
        var mask = new byte[4];
        for (var i = 0; i < 32; i++) if (i < prefix) mask[i / 8] |= (byte)(1 << (7 - (i % 8)));
        return mask;
    }

    /// <summary>Validates a user-entered CIDR (a.b.c.d/0-32).</summary>
    public static bool IsValidCidr(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr)) return false;
        var parts = cidr.Split('/');
        return parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            && int.TryParse(parts[1], out var n) && n is >= 0 and <= 32;
    }
}
