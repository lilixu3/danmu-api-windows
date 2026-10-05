using System.Net;
using DanmuApi.Core.Frp;
using DanmuApi.Runtime.Frp;

namespace DanmuApi.App.Services;

internal static class FrpEndpointMetadata
{
    internal static FrpProxyKind? ApiKind(FrpSnapshot snapshot, FrpSettings settings, int? corePort)
    {
        if (settings.Role != FrpRole.Client) return null;
        if (settings.ConfigMode != FrpConfigMode.Text) return settings.Client.ProxyKind;
        if (snapshot.State != FrpTunnelState.Running || corePort is not > 0 || string.IsNullOrEmpty(snapshot.RemoteAddress))
            return null;

        // A native document may tunnel unrelated services; never append the API token to those endpoints.
        var target = snapshot.ExpectedProxies.FirstOrDefault(expected =>
            expected.LocalPort == corePort && IsLoopback(expected.LocalAddress)
            && snapshot.ProxyList.Any(proxy => proxy.Name == expected.AdminName && proxy.IsRunning
                && proxy.Type == expected.Type && proxy.RemoteAddress == snapshot.RemoteAddress));
        return target?.Type switch
        {
            "tcp" => FrpProxyKind.Tcp,
            "http" => FrpProxyKind.Http,
            "https" => FrpProxyKind.Https,
            _ => null,
        };
    }

    private static bool IsLoopback(string address) => address.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip));
}
