using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DanmuApi.Platform;

public sealed record RuntimeIpv6Candidate(
    IPAddress Address,
    bool InterfaceUp,
    bool HasIpv6DefaultGateway,
    NetworkInterfaceType InterfaceType,
    string InterfaceId,
    string InterfaceName,
    string InterfaceDescription,
    PrefixOrigin PrefixOrigin,
    SuffixOrigin SuffixOrigin,
    DuplicateAddressDetectionState DadState);

public static class RuntimeNetworkAddressResolver
{
    private static readonly string[] VirtualInterfaceMarkers =
    [
        "virtual",
        "hyper-v",
        "vmware",
        "virtualbox",
        "vethernet",
        "tap",
        "tun",
        "teredo",
        "isatap",
        "6to4",
        "wsl",
        "vpn",
    ];

    public static IReadOnlyList<string> ResolveActiveIpv4Addresses() =>
        ResolveActiveAddresses(AddressFamily.InterNetwork, IsApipa);

    public static IReadOnlyList<string> ResolveActiveIpv6Addresses() =>
        SelectShareableIpv6Candidates(EnumerateIpv6Candidates())
            .Select(candidate => candidate.Address.ToString())
            .ToArray();

    /// <summary>对齐移动端：从活动网卡中选出一个首选 IPv4（优先有网关的以太网/Wi-Fi）。</summary>
    public static string? ResolvePrimaryIpv4Address() =>
        ResolvePrimaryAddress(AddressFamily.InterNetwork, IsApipa);

    /// <summary>优先选择有默认路由的稳定全球地址，也支持无默认路由的稳定局域网 ULA。</summary>
    public static string? ResolvePrimaryIpv6Address() =>
        SelectShareableIpv6Candidates(EnumerateIpv6Candidates())
            .Select(candidate => candidate.Address.ToString())
            .FirstOrDefault();

    public static IReadOnlyList<RuntimeIpv6Candidate> SelectStableGlobalIpv6Candidates(
        IEnumerable<RuntimeIpv6Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(IsStableGlobalIpv6Candidate)
            .OrderBy(candidate => InterfaceRank(candidate.InterfaceType))
            .ThenBy(candidate => SuffixStabilityRank(candidate.SuffixOrigin))
            .ThenBy(candidate => candidate.InterfaceId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<RuntimeIpv6Candidate> SelectShareableIpv6Candidates(
        IEnumerable<RuntimeIpv6Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(candidate => IsStableGlobalIpv6Candidate(candidate) ||
                (IsStablePhysicalIpv6Candidate(candidate) && IsUniqueLocalAddress(candidate.Address)))
            .OrderBy(candidate => IsUniqueLocalAddress(candidate.Address) ? 1 : 0)
            .ThenBy(candidate => InterfaceRank(candidate.InterfaceType))
            .ThenBy(candidate => SuffixStabilityRank(candidate.SuffixOrigin))
            .ThenBy(candidate => candidate.InterfaceId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    public static bool IsUniqueLocalAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static string? ResolvePrimaryAddress(AddressFamily family, Func<IPAddress, bool> excluded)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(HasGateway)
            .ThenBy(networkInterface => InterfaceRank(networkInterface.NetworkInterfaceType))
            .ThenBy(networkInterface => networkInterface.Id, StringComparer.Ordinal);

        foreach (var networkInterface in interfaces)
        {
            foreach (var unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicastAddress.Address;
                if (address.AddressFamily != family ||
                    IPAddress.IsLoopback(address) ||
                    excluded(address) ||
                    address.Equals(IPAddress.Any) ||
                    address.Equals(IPAddress.IPv6Any) ||
                    address.IsIPv6LinkLocal ||
                    unicastAddress.DuplicateAddressDetectionState is
                        DuplicateAddressDetectionState.Tentative or DuplicateAddressDetectionState.Duplicate)
                {
                    continue;
                }

                return address.ToString();
            }
        }

        return null;
    }

    private static IEnumerable<RuntimeIpv6Candidate> EnumerateIpv6Candidates()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = networkInterface.GetIPProperties();
            var hasIpv6DefaultGateway = properties.GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                !IPAddress.IsLoopback(gateway.Address) &&
                !gateway.Address.Equals(IPAddress.IPv6Any));
            var interfaceUp = networkInterface.OperationalStatus == OperationalStatus.Up;
            foreach (var unicastAddress in properties.UnicastAddresses)
            {
                yield return new RuntimeIpv6Candidate(
                    unicastAddress.Address,
                    interfaceUp,
                    hasIpv6DefaultGateway,
                    networkInterface.NetworkInterfaceType,
                    networkInterface.Id,
                    networkInterface.Name,
                    networkInterface.Description,
                    unicastAddress.PrefixOrigin,
                    unicastAddress.SuffixOrigin,
                    unicastAddress.DuplicateAddressDetectionState);
            }
        }
    }

    private static bool IsStableGlobalIpv6Candidate(RuntimeIpv6Candidate candidate) =>
        IsStablePhysicalIpv6Candidate(candidate) &&
        candidate.HasIpv6DefaultGateway &&
        IsGlobalUnicast(candidate.Address) &&
        !IsTransitionOrDocumentationAddress(candidate.Address);

    private static bool IsStablePhysicalIpv6Candidate(RuntimeIpv6Candidate candidate) =>
        candidate.InterfaceUp &&
        !IsVirtualOrTunnelInterface(candidate) &&
        candidate.DadState == DuplicateAddressDetectionState.Preferred &&
        candidate.SuffixOrigin != SuffixOrigin.Random &&
        candidate.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
        !IPAddress.IsLoopback(candidate.Address) &&
        !candidate.Address.IsIPv6LinkLocal;

    private static bool IsGlobalUnicast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 && (bytes[0] & 0xE0) == 0x20;
    }

    private static bool IsTransitionOrDocumentationAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 &&
               ((bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00) ||
                (bytes[0] == 0x20 && bytes[1] == 0x02) ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8));
    }

    private static bool IsVirtualOrTunnelInterface(RuntimeIpv6Candidate candidate)
    {
        if (candidate.InterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return true;
        }

        var identity = $"{candidate.InterfaceName} {candidate.InterfaceDescription}";
        return VirtualInterfaceMarkers.Any(marker =>
            identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static int SuffixStabilityRank(SuffixOrigin origin) => origin switch
    {
        SuffixOrigin.Manual => 0,
        SuffixOrigin.OriginDhcp => 1,
        SuffixOrigin.LinkLayerAddress => 2,
        SuffixOrigin.Other => 3,
        _ => 4,
    };

    private static bool HasGateway(NetworkInterface networkInterface) =>
        networkInterface.GetIPProperties().GatewayAddresses.Count > 0;

    private static int InterfaceRank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        _ => 2,
    };

    private static IReadOnlyList<string> ResolveActiveAddresses(
        AddressFamily family,
        Func<IPAddress, bool> excluded)
    {
        var addresses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicastAddress in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicastAddress.Address;
                if (address.AddressFamily != family ||
                    IPAddress.IsLoopback(address) ||
                    excluded(address) ||
                    address.Equals(IPAddress.Any) ||
                    address.Equals(IPAddress.IPv6Any) ||
                    address.IsIPv6LinkLocal)
                {
                    continue;
                }

                var text = address.ToString();
                if (seen.Add(text))
                {
                    addresses.Add(text);
                }
            }
        }

        return new ReadOnlyCollection<string>(addresses);
    }

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
