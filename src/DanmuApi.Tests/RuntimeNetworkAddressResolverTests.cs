using System.Net;
using System.Net.NetworkInformation;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeNetworkAddressResolverTests
{
    [Fact]
    public void ReturnsReadOnlyValidIpv6AddressesWithoutLoopbackOrLinkLocal()
    {
        var addresses = RuntimeNetworkAddressResolver.ResolveActiveIpv6Addresses();

        Assert.NotNull(addresses);
        Assert.Equal(addresses.Count, addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(addresses, address =>
        {
            Assert.True(IPAddress.TryParse(address, out var parsed));
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetworkV6, parsed!.AddressFamily);
            Assert.False(IPAddress.IsLoopback(parsed));
            Assert.False(parsed.IsIPv6LinkLocal);
        });

        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)addresses).Add("2001:db8::1"));
    }

    [Fact]
    public void ReturnsReadOnlyValidIpv4AddressesWithoutLoopbackOrApipa()
    {
        var addresses = RuntimeNetworkAddressResolver.ResolveActiveIpv4Addresses();

        Assert.NotNull(addresses);
        Assert.IsAssignableFrom<IReadOnlyList<string>>(addresses);
        Assert.Equal(addresses.Count, addresses.Distinct(StringComparer.Ordinal).Count());
        Assert.All(addresses, address =>
        {
            Assert.True(IPAddress.TryParse(address, out var parsed));
            Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, parsed!.AddressFamily);
            Assert.False(IPAddress.IsLoopback(parsed));
            Assert.False(IsApipa(parsed));
        });

        Assert.Throws<NotSupportedException>(() => ((ICollection<string>)addresses).Add("192.0.2.1"));
    }

    [Fact]
    public void PrimaryIpv4MatchesListAndPrefersGatewayInterfaces()
    {
        var primary = RuntimeNetworkAddressResolver.ResolvePrimaryIpv4Address();
        var all = RuntimeNetworkAddressResolver.ResolveActiveIpv4Addresses();

        if (all.Count == 0)
        {
            Assert.Null(primary);
            return;
        }

        Assert.NotNull(primary);
        Assert.Contains(primary!, all);
    }

    [Fact]
    public void SelectsOnlyStableGlobalIpv6OnPhysicalRoutedInterface()
    {
        var candidates = new[]
        {
            Candidate("2001:4860:1::10", NetworkInterfaceType.Ethernet, SuffixOrigin.Random),
            Candidate("2001:4860:1::11", NetworkInterfaceType.Ethernet, SuffixOrigin.LinkLayerAddress),
            Candidate("fc00::10", NetworkInterfaceType.Ethernet, SuffixOrigin.Manual),
            Candidate("2001:4860:1::12", NetworkInterfaceType.Tunnel, SuffixOrigin.Manual),
            Candidate("2001:4860:1::13", NetworkInterfaceType.Ethernet, SuffixOrigin.Manual, dadState: DuplicateAddressDetectionState.Tentative),
            Candidate("2002:c000:0204::1", NetworkInterfaceType.Ethernet, SuffixOrigin.Manual),
        };

        var selected = RuntimeNetworkAddressResolver.SelectStableGlobalIpv6Candidates(candidates);

        var only = Assert.Single(selected);
        Assert.Equal("2001:4860:1::11", only.Address.ToString());
    }

    [Fact]
    public void StableIpv6SelectionIsDeterministicAndPrefersPhysicalEthernet()
    {
        var candidates = new[]
        {
            Candidate("2001:4860:2::20", NetworkInterfaceType.Wireless80211, SuffixOrigin.Manual, interfaceId: "wifi"),
            Candidate("2001:4860:1::20", NetworkInterfaceType.Ethernet, SuffixOrigin.Manual, interfaceId: "ethernet"),
        };

        var selected = RuntimeNetworkAddressResolver.SelectStableGlobalIpv6Candidates(candidates);

        Assert.Equal("2001:4860:1::20", selected[0].Address.ToString());
    }

    [Fact]
    public void NoStableGlobalIpv6ReturnsEmptySelection()
    {
        var selected = RuntimeNetworkAddressResolver.SelectStableGlobalIpv6Candidates(
        [
            Candidate("fe80::1", NetworkInterfaceType.Ethernet, SuffixOrigin.Manual),
            Candidate("2001:4860::1", NetworkInterfaceType.Ethernet, SuffixOrigin.Random),
        ]);

        Assert.Empty(selected);
    }

    [Fact]
    public void PrimaryIpv6MatchesListAndExcludesLinkLocal()
    {
        var primary = RuntimeNetworkAddressResolver.ResolvePrimaryIpv6Address();
        var all = RuntimeNetworkAddressResolver.ResolveActiveIpv6Addresses();

        if (all.Count == 0)
        {
            Assert.Null(primary);
            return;
        }

        Assert.NotNull(primary);
        Assert.Contains(primary!, all);
        Assert.True(IPAddress.TryParse(primary, out var parsed));
        Assert.False(parsed!.IsIPv6LinkLocal);
    }

    [Fact]
    public void LocalIpv6DoesNotRequireInternetGatewayAndGlobalAddressesRemainPreferred()
    {
        var ula = Candidate("fd75:7662::10", NetworkInterfaceType.Ethernet, SuffixOrigin.OriginDhcp) with { HasIpv6DefaultGateway = false };
        var global = Candidate("2001:4860::10", NetworkInterfaceType.Wireless80211, SuffixOrigin.Manual);
        var selected = RuntimeNetworkAddressResolver.SelectShareableIpv6Candidates(
        [
            ula,
            ula with { Address = IPAddress.Parse("fd75:7662::11"), SuffixOrigin = SuffixOrigin.Random },
            ula with { Address = IPAddress.Parse("fd75:7662::12"), DadState = DuplicateAddressDetectionState.Deprecated },
            ula with { Address = IPAddress.Parse("fd75:7662::13"), InterfaceUp = false },
            ula with { Address = IPAddress.Parse("fd75:7662::14"), InterfaceType = NetworkInterfaceType.Tunnel },
            global,
            global with { Address = IPAddress.Parse("2001:4860::11"), HasIpv6DefaultGateway = false },
        ]);

        Assert.Equal(new[] { global, ula }, selected);
        Assert.Equal(ula, Assert.Single(RuntimeNetworkAddressResolver.SelectShareableIpv6Candidates([ula])));
        Assert.True(RuntimeNetworkAddressResolver.IsUniqueLocalAddress(ula.Address));
        Assert.False(RuntimeNetworkAddressResolver.IsUniqueLocalAddress(global.Address));
    }

    private static RuntimeIpv6Candidate Candidate(
        string address,
        NetworkInterfaceType interfaceType,
        SuffixOrigin suffixOrigin,
        string interfaceId = "ethernet",
        DuplicateAddressDetectionState dadState = DuplicateAddressDetectionState.Preferred) =>
        new(
            IPAddress.Parse(address),
            InterfaceUp: true,
            HasIpv6DefaultGateway: true,
            interfaceType,
            interfaceId,
            interfaceId,
            interfaceId,
            PrefixOrigin.RouterAdvertisement,
            suffixOrigin,
            dadState);

    private static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
