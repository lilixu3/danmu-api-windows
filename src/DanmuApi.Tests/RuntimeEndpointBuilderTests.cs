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
    public void EncodesTokenSegmentTheWayTheCoreComparesIt()
    {
        // 核心拿未解码的路径段与令牌全等比较（worker.js），所以浏览器不会转的字符宿主也不能转：
        // 旧实现用 Uri.EscapeDataString，把 ! ' ( ) * & = + , ; : $ @ 全变成 %XX，
        // 含这些字符的令牌在核心自家网页能用、在宿主这一侧会全线 401。
        Assert.Equal(
            "http://192.168.1.20:8080/a!b'c(d)*e&f=g+h,i;j:k@m$n_p~q",
            RuntimeEndpointBuilder.BuildApiAddress(
                "192.168.1.20", 8080, " a!b'c(d)*e&f=g+h,i;j:k@m$n_p~q "));

        // 路径里非转不可的字符仍然要转（与浏览器写路径的结果一致）。
        Assert.Equal(
            "http://192.168.1.20:8080/a%20b%22c%3Cd%3Ee%60f%7Bg%7Dh",
            RuntimeEndpointBuilder.BuildApiAddress("192.168.1.20", 8080, "a b\"c<d>e`f{g}h"));
        Assert.Equal(
            "http://192.168.1.20:8080/%E5%BC%B9%E5%B9%95",
            RuntimeEndpointBuilder.BuildApiAddress("192.168.1.20", 8080, "弹幕"));
    }

    [Theory]
    [InlineData("a/b c")]
    [InlineData("token?x")]
    [InlineData("token#y")]
    public void RejectsTokenCharactersThatCannotFormAPathSegment(string token)
    {
        // / # ? 会改变 URL 结构，核心侧永远比对不上；与其让每个页面都收到莫名 401，
        // 不如在构造地址时就说明是哪个字符、该怎么办。
        var error = Assert.Throws<ArgumentException>(() =>
            RuntimeEndpointBuilder.BuildApiAddress("127.0.0.1", 9321, token));
        Assert.Contains("路径前缀", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenTextFormsCoverEveryShapeThatCanReachAText()
    {
        // 脱敏必须同时挡住原文、路径段编码与全量转义三种写法，漏一种就会把密钥留在日志/界面里。
        var forms = RuntimeEndpointBuilder.TokenTextForms("a b!c").ToArray();
        Assert.Contains("a b!c", forms);
        Assert.Contains("a%20b!c", forms);
        Assert.Contains("a%20b%21c", forms);
        Assert.All(forms, form => Assert.True(form.Length > 0));
        Assert.Equal(forms.OrderByDescending(form => form.Length).ToArray(), forms);
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
