using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class GithubRepositoryReferenceTests
{
    [Theory]
    [InlineData("https://github.com/lilixu3/danmu_api/tree/test", "lilixu3", "danmu_api", "test")]
    [InlineData("https://github.com/lilixu3/danmu_api/tree/feature/core-ui", "lilixu3", "danmu_api", "feature/core-ui")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo", null)]
    [InlineData("owner/repo", "owner", "repo", null)]
    public void ParsesSupportedRepositoryInputs(
        string input,
        string owner,
        string repository,
        string? branch)
    {
        var result = GithubRepositoryReference.Parse(input);

        Assert.Equal(owner, result.Owner);
        Assert.Equal(repository, result.Repository);
        Assert.Equal(branch, result.Branch);
    }

    [Fact]
    public void RecognizesOfficialRepositoryWithoutChangingCaseInsensitiveIdentity()
    {
        var result = GithubRepositoryReference.Parse("https://github.com/Huangxd-/Danmu_Api/tree/main");

        Assert.Equal(CoreRepositorySource.Official, result.Source);
        Assert.Equal("Huangxd-/Danmu_Api", result.FullName);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://evil.example/owner/repo")]
    [InlineData("https://github.com/owner/repo/issues")]
    [InlineData("https://github.com/owner/repo/tree")]
    [InlineData("https://github.com/owner/repo/tree/feature..bad")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("owner")]
    public void RejectsUnsupportedOrAmbiguousInputs(string input)
    {
        Assert.Throws<FormatException>(() => GithubRepositoryReference.Parse(input));
    }

    [Theory]
    [InlineData("refs/heads/feature/windows", "feature/windows")]
    [InlineData("release-1.0", "release-1.0")]
    public void NormalizesValidBranchNames(string input, string expected)
    {
        Assert.Equal(expected, GithubRepositoryReference.ValidateBranch(input));
    }
}
