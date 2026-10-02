using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class WindowsProcessArgumentsTests
{
    [Theory]
    [InlineData("-c \"C:\\owned\\frpc.toml\"", true)]
    [InlineData("--config C:/owned/frpc.toml", true)]
    [InlineData("--config=\"C:\\owned\\frpc.toml\"", true)]
    [InlineData("-c=\"C:\\owned\\frpc.toml\"", true)]
    [InlineData("-c \"C:\\owned\\frpc.toml.manual\"", false)]
    [InlineData("-c \"C:\\other\\frpc.toml\" --token C:\\owned\\frpc.toml", false)]
    [InlineData("tcp --token SECRET -c C:\\owned\\frpc.toml", false)]
    [InlineData("verify -c C:\\owned\\frpc.toml", false)]
    [InlineData("-c C:\\owned\\frpc.toml --config=C:\\other\\frpc.toml", false)]
    [InlineData("-c frpc.toml", false)]
    [InlineData("-c \"C:\\owned\\frpc.toml", false)]
    public void FrpRequiresAnExactUnambiguousConfigArgument(string arguments, bool matches)
    {
        Assert.Equal(matches, WindowsProcessArguments.MatchesFrpConfig("\"C:\\app\\frpc.exe\" " + arguments, @"C:\owned\frpc.toml"));
    }

    [Theory]
    [InlineData("\"C:\\owned\\main.js\"", true)]
    [InlineData("C:/owned/main.js", true)]
    [InlineData("C:\\owned\\main.js.manual", false)]
    [InlineData("--eval C:\\owned\\main.js", false)]
    [InlineData("C:\\other\\main.js C:\\owned\\main.js", false)]
    [InlineData("--require C:\\owned\\main.js C:\\other\\main.js", false)]
    [InlineData("main.js", false)]
    public void NodeRequiresThePositionalEntrypointNotAnOptionOrScriptArgument(string arguments, bool matches)
    {
        Assert.Equal(matches, WindowsProcessArguments.MatchesNodeEntryPoint("node.exe " + arguments, @"C:\owned\main.js"));
    }

    [Fact]
    public void ParserPreservesSpacesUnicodeAndBackslashQuoteRules()
    {
        Assert.True(WindowsProcessArguments.TryParse("\"C:\\Program Files\\frpc.exe\"\t-c \"C:\\用户 配置\\frpc.toml\"", out var arguments));
        Assert.Equal(new[] { @"C:\Program Files\frpc.exe", "-c", @"C:\用户 配置\frpc.toml" }, arguments);
        Assert.True(WindowsProcessArguments.MatchesFrpConfig("frpc.exe --config=\"C:\\用户 配置\\sub\\..\\frpc.toml\"", @"C:\用户 配置\frpc.toml"));
        Assert.True(WindowsProcessArguments.TryParse("app.exe \"a\\\"b\" \"C:\\end\\\\\"", out var escaped));
        Assert.Equal(new[] { "app.exe", "a\"b", @"C:\end\" }, escaped);
        Assert.False(WindowsProcessArguments.TryParse("app.exe \"unfinished", out _));
        Assert.False(WindowsProcessArguments.TryParse("app.exe\0 --token SECRET", out _));
    }
}
