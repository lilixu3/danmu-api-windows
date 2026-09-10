using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class GithubProxyCatalogTests
{
    [Fact]
    public void OptionsMatchAndroidIdentifiersExactly()
    {
        Assert.Equal(
            ["original", "gh_proxy_org", "hk_gh_proxy", "cdn_gh_proxy", "edgeone_gh_proxy"],
            GithubProxyCatalog.Options.Select(option => option.Id));
    }

    [Fact]
    public void OriginalRouteUsesOnlyOriginalUrl()
    {
        var original = new Uri("https://api.github.com/repos/owner/repo/zipball/main");

        var candidates = GithubProxyCatalog.BuildDownloadCandidates("original", original);

        Assert.Single(candidates);
        Assert.Equal(original, candidates[0]);
    }

    [Theory]
    [InlineData("https://proxy.example/{url}", "https://proxy.example/https://github.com/owner/repo/archive/main.zip")]
    [InlineData("https://proxy.example/%s", "https://proxy.example/https://github.com/owner/repo/archive/main.zip")]
    public void TemplateRoutesBuildExpectedFirstCandidate(string baseUrl, string expected)
    {
        var candidates = GithubProxyCatalog.BuildProxyCandidates(
            baseUrl,
            new Uri("https://github.com/owner/repo/archive/main.zip"));

        Assert.Equal(expected, candidates[0].AbsoluteUri);
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }

    [Fact]
    public void QueryRouteEscapesOriginalUrl()
    {
        var candidates = GithubProxyCatalog.BuildProxyCandidates(
            "https://proxy.example/?url=",
            new Uri("https://github.com/owner/repo/archive/feature%2Fone.zip"));

        Assert.StartsWith("https://proxy.example/?url=https%3A%2F%2Fgithub.com", candidates[0].AbsoluteUri, StringComparison.Ordinal);
    }
}
