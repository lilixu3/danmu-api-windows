using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

/// <summary>Covers the runtime-identity policy introduced for the Node 22 bundle: the engines floor
/// matcher, the floor-to-issue conversion, and the requirement that a bundle declares which Node
/// runtime it carries.</summary>
public sealed class BundledNodeRuntimePolicyTests
{
    [Theory]
    [InlineData(">=18", "22.23.2", true)]
    [InlineData(">= 14", "22.23.2", true)]
    [InlineData(">= 14.16.0", "22.23.2", true)]
    [InlineData(">=12", "22.23.2", true)]
    [InlineData("^12.20.0 || ^14.13.1 || >=16.0.0", "22.23.2", true)]
    [InlineData("^22.0.0", "22.23.2", true)]
    [InlineData("~22.23", "22.23.2", true)]
    [InlineData("22", "22.23.2", true)]
    [InlineData("*", "22.23.2", true)]
    [InlineData(">=24", "22.23.2", false)]
    [InlineData(">=18 <21", "22.23.2", false)]
    [InlineData("~22.1", "22.23.2", false)]
    [InlineData("24", "22.23.2", false)]
    public void EngineRangeMatchesTheSubsetThatEnginesNodeUses(string range, string running, bool expected)
    {
        Assert.Equal(expected, NodeEngineRange.TrySatisfies(range, running, out var problem));
        if (expected) Assert.Null(problem);
        else Assert.NotNull(problem);
    }

    /// <summary>A range shape this host does not understand must be reported, never assumed
    /// satisfied: silently passing is what turns a future core into a start-up crash.</summary>
    [Theory]
    [InlineData(">=20.x")]
    [InlineData(">=20.0.0-rc.1")]
    [InlineData(">=18 ||")]
    [InlineData("lts/*")]
    public void UnsupportedEngineRangesAreReportedInsteadOfAssumedSatisfied(string range)
    {
        Assert.False(NodeEngineRange.TrySatisfies(range, "22.23.2", out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void DeclaredEngineFloorAboveTheBundledNodeBecomesAnExplicitIssue()
    {
        var requirements = new List<CoreDependencyEngineRequirement>
        {
            new("satisfied-dep", ">=18"),
            new("future-dep", ">=24.0.0"),
        };
        var result = CoreDependencyService.ApplyEngineFloors(new(2, [], requirements));

        Assert.False(result.IsHealthy);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("future-dep", issue.Name);
        Assert.Contains(">=24.0.0", issue.Diagnostic, StringComparison.Ordinal);
        Assert.Contains(BundledNodeRuntime.ExpectedVersion, issue.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthyEngineFloorsAddNoIssues()
    {
        var result = CoreDependencyService.ApplyEngineFloors(new(1, [], [new("dep", ">=18")]));
        Assert.True(result.IsHealthy);
    }

    [Fact]
    public void BundledNodeVersionComesFromTheBuildAndIsUsable()
    {
        Assert.Equal(22, BundledNodeRuntime.ExpectedMajor);
        Assert.Equal(22, BundledNodeRuntime.ParseMajor("v22.23.2"));
        Assert.Equal(24, BundledNodeRuntime.ParseMajor("24"));
        Assert.Throws<InvalidOperationException>(() => BundledNodeRuntime.ParseMajor("node22"));
        Assert.Throws<InvalidOperationException>(() => BundledNodeRuntime.ParseMajor(""));
    }

    /// <summary>A bundle that does not say which Node runtime it carries, or that carries one for a
    /// different architecture or version, is a packaging error and must fail instead of deploying.</summary>
    [Fact]
    public void RuntimeIdentityMustMatchTheHostAndTheDeclaredNodeVersion()
    {
        var host = BundledRuntimePreparer.HostArchitecture();
        var other = host == "x64" ? "arm64" : "x64";
        var cases = new (string? NodeVersion, string? Arch, string Expected)[]
        {
            (null, host, "缺少"),
            (BundledNodeRuntime.ExpectedVersion, null, "缺少"),
            (BundledNodeRuntime.ExpectedVersion, other, "不匹配"),
            ("24.19.0", host, "不匹配"),
        };

        foreach (var item in cases)
        {
            using var directory = new TemporaryDirectory();
            var bundle = Path.Combine(directory.Path, "bundle");
            Directory.CreateDirectory(bundle);
            WriteBundle(bundle, item.NodeVersion, item.Arch, version: new string('A', 64));
            var paths = new AppPaths(Path.Combine(directory.Path, "runtime"), Path.Combine(directory.Path, "settings"));

            var error = Assert.Throws<InvalidOperationException>(() => BundledRuntimePreparer.Prepare(bundle, paths));
            Assert.Contains(item.Expected, error.Message, StringComparison.Ordinal);
        }
    }

    private static void WriteBundle(string bundle, string? nodeVersion, string? arch, string version)
    {
        var hash = new string('B', 64);
        // The manifest must carry the Node executable and the start-up entry, same as a real bundle.
        File.WriteAllText(Path.Combine(bundle, "SHA256SUMS.txt"),
            hash + "  node.exe" + Environment.NewLine + hash + "  nodejs-project/main.js");
        var nodeVersionJson = nodeVersion is null ? "null" : "\"" + nodeVersion + "\"";
        var archJson = arch is null ? "null" : "\"" + arch + "\"";
        File.WriteAllText(Path.Combine(bundle, "runtime-build.json"),
            "{\"Schema\":1,\"Version\":\"" + version + "\",\"NodeVersion\":" + nodeVersionJson +
            ",\"Arch\":" + archJson + ",\"Files\":[{\"Path\":\"node.exe\",\"Hash\":\"" + hash + "\",\"Mode\":\"hash\"}," +
            "{\"Path\":\"nodejs-project/main.js\",\"Hash\":\"" + hash + "\",\"Mode\":\"hash\"}]}");
    }
}
