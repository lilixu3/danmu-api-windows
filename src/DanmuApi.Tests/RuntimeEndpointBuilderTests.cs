using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class RuntimeEndpointBuilderTests
{
    [Fact]
    public void UsesFallbackTokenWhenTokenIsNullOrWhitespace()
    {
        Assert.Equal(
            "http://127.0.0.1:9321/87654321",
            RuntimeEndpointBuilder.BuildApiAddress("127.0.0.1", 9321, null));
        Assert.Equal(
            "http://127.0.0.1:9321/87654321",
            RuntimeEndpointBuilder.BuildApiAddress("127.0.0.1", 9321, "  "));
    }

    [Fact]
    public void TrimsAndEscapesExplicitToken()
    {
        var result = RuntimeEndpointBuilder.BuildApiAddress("192.168.1.20", 8080, " a/b c?x#y ");

        Assert.Equal("http://192.168.1.20:8080/a%2Fb%20c%3Fx%23y", result);
    }

    [Fact]
    public void BracketsRawIpv6AndDoesNotDuplicateExistingBrackets()
    {
        Assert.Equal(
            "http://[2001:db8::1]:9321/token",
            RuntimeEndpointBuilder.BuildApiAddress("2001:db8::1", 9321, "token"));
        Assert.Equal(
            "http://[2001:db8::1]:9321/token",
            RuntimeEndpointBuilder.BuildApiAddress("[2001:db8::1]", 9321, "token"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("host name")]
    [InlineData("http://127.0.0.1")]
    [InlineData("127.0.0.1/path")]
    [InlineData("[2001:db8::1")]
    [InlineData("2001:db8::not-an-ip")]
    [InlineData("[127.0.0.1]")]
    public void RejectsInvalidHost(string? host)
    {
        Assert.ThrowsAny<ArgumentException>(() => RuntimeEndpointBuilder.BuildApiAddress(host!, 9321, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void RejectsInvalidPort(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeEndpointBuilder.BuildApiAddress("127.0.0.1", port, null));
    }
}
