using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class CoreInstallationModelsTests
{
    public static CoreInstallationManifest Source() => new(
        2, ManagedCoreVariant.Stable, "huangxd-/danmu_api", "main", new string('a', 40),
        "1.0.0", "core", CoreInstallKind.LocalPullRequestStack, null,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"))
    {
        BaseCommitSha = new string('a', 40),
        LocalMergeSha = new string('b', 40),
        PullRequests =
        [
            new(12, "fork/core", "feature", new string('c', 40), new string('a', 40)),
            new(13, "other/core", "next", new string('d', 40), null),
        ],
    };

    public static IEnumerable<object[]> ChangedSourceFields() => new[]
    {
        "schema", "variant", "repository", "branch", "commit", "version", "name", "kind", "number",
        "installedAt", "baseCommit", "mergeCommit", "prOrder", "prCount", "prNumber", "prRepository",
        "prBranch", "prHead", "prBase",
    }.Select(field => new object[] { field });

    public static CoreInstallationManifest Change(CoreInstallationManifest value, string field) => field switch
    {
        "schema" => value with { SchemaVersion = 1 },
        "variant" => value with { Variant = ManagedCoreVariant.Dev },
        "repository" => value with { Repository = "other/core" },
        "branch" => value with { Branch = "next" },
        "commit" => value with { CommitSha = new string('e', 40) },
        "version" => value with { Version = "1.0.1" },
        "name" => value with { DisplayName = "renamed" },
        "kind" => value with { InstallKind = CoreInstallKind.Rollback },
        "number" => value with { PullRequestNumber = 12 },
        "installedAt" => value with { InstalledAt = value.InstalledAt.AddSeconds(1) },
        "baseCommit" => value with { BaseCommitSha = new string('e', 40) },
        "mergeCommit" => value with { LocalMergeSha = new string('e', 40) },
        "prOrder" => value with { PullRequests = value.PullRequests.Reverse().ToArray() },
        "prCount" => value with { PullRequests = value.PullRequests.Take(1).ToArray() },
        "prNumber" => ChangeFirst(value, value.PullRequests[0] with { Number = 14 }),
        "prRepository" => ChangeFirst(value, value.PullRequests[0] with { HeadRepository = "new/core" }),
        "prBranch" => ChangeFirst(value, value.PullRequests[0] with { HeadBranch = "other" }),
        "prHead" => ChangeFirst(value, value.PullRequests[0] with { HeadSha = new string('e', 40) }),
        "prBase" => ChangeFirst(value, value.PullRequests[0] with { BaseSha = new string('e', 40) }),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static CoreInstallationManifest ChangeFirst(CoreInstallationManifest value, CorePullRequestSource first) =>
        value with { PullRequests = new[] { first }.Concat(value.PullRequests.Skip(1)).ToArray() };

    [Theory]
    [MemberData(nameof(ChangedSourceFields))]
    public void AuditCompleteSourceIdentityIncludesEveryFieldAndOrderedPullRequestContent(string field)
    {
        var source = Source();
        Assert.False(CoreInstallationManifest.SourcesEqual(source, Change(source, field)));
    }

    [Fact]
    public void AuditSourceIdentityComparesListContentsInsteadOfListReferences()
    {
        var source = Source();
        Assert.True(CoreInstallationManifest.SourcesEqual(source,
            source with { PullRequests = source.PullRequests.Select(item => item with { }).ToArray() }));
        Assert.True(CoreInstallationManifest.SourcesEqual(null, null));
        Assert.False(CoreInstallationManifest.SourcesEqual(source, null));
    }
}
