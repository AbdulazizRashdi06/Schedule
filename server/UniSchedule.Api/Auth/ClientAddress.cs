using System.Net;
using System.Net.Sockets;

namespace UniSchedule.Api.Auth;

public static class ClientAddress
{
    /// <summary>The client's address for per-address limits; for IPv6 its /64, which is what one home or phone
    /// connection gets (so one connection can't pick a fresh address for every try).</summary>
    public static string Key(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
