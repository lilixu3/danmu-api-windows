using System.Net;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class GithubCoreRemoteTests
{
    [Fact]
    public async Task RepositoryRequestUsesOfficialApiAndBearerToken()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((request, _) =>
        {
            captured = CloneRequest(request);
            return JsonResponse("""
                {
                  "full_name": "huangxd-/danmu_api",
                  "default_branch": "main",
                  "description": "core",
                  "private": false
                }
                """, rateRemaining: 42);
        });
        var remote = CreateRemote(handler, "secret-token");

        var result = await remote.GetRepositoryAsync(GithubRepositoryReference.Official());

        Assert.Equal("huangxd-/danmu_api", result.FullName);
        Assert.NotNull(captured);
        Assert.Equal("api.github.com", captured!.RequestUri!.Host);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", captured.Headers.Authorization.Parameter);
        Assert.Equal(42, remote.LastRateLimit!.Remaining);
        Assert.True(remote.LastRateLimit.Authenticated);
    }

    [Fact]
    public async Task ValidateTokenUsesOverrideWithoutPersistingIt()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHandler((request, _) =>
        {
            requests.Add(CloneRequest(request));
            return JsonResponse("{\"login\":\"octocat\"}");
        });
        var remote = CreateRemote(handler, null);

        var login = await remote.ValidateTokenAsync("token temporary-secret");

        Assert.Equal("octocat", login);
        Assert.Single(requests);
        Assert.Equal("temporary-secret", requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task InvalidFieldTypeFailsAsProtocolError()
    {
        var remote = CreateRemote(new StubHandler((_, _) => JsonResponse("""
            {
              "full_name": "owner/repo",
              "default_branch": 42,
              "private": false
            }
            """)), null);

        var error = await Assert.ThrowsAsync<GithubRemoteException>(() =>
            remote.GetRepositoryAsync(GithubRepositoryReference.Parse("owner/repo")));

        Assert.Equal(GithubFailureKind.Protocol, error.Kind);
        Assert.Contains("default_branch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RateLimitBodyIsParsedStrictly()
    {
        var remote = CreateRemote(new StubHandler((_, _) => JsonResponse("""
            {
              "resources": {
                "core": {
                  "limit": 60,
                  "remaining": 37,
                  "used": 23,
                  "reset": 1788230400
                }
              }
            }
            """)), null);

        var rate = await remote.GetRateLimitAsync();

        Assert.Equal(60, rate.Limit);
        Assert.Equal(37, rate.Remaining);
        Assert.Equal(23, rate.Used);
        Assert.False(rate.Authenticated);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GithubFailureKind.Authentication)]
    [InlineData(HttpStatusCode.NotFound, GithubFailureKind.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, GithubFailureKind.RateLimited)]
    public async Task HttpFailuresKeepClassification(HttpStatusCode status, GithubFailureKind kind)
    {
        var remote = CreateRemote(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("{\"message\":\"denied\"}"),
        })), null);

        var error = await Assert.ThrowsAsync<GithubRemoteException>(() =>
            remote.GetRepositoryAsync(GithubRepositoryReference.Official()));

        Assert.Equal(kind, error.Kind);
        Assert.Equal((int)status, error.StatusCode);
    }

    [Fact]
    public async Task NetworkDiagnosticRedactsToken()
    {
        const string token = "network-secret-token";
        var remote = CreateRemote(new StubHandler((_, _) =>
            throw new HttpRequestException($"failed with {token}")), token);

        var error = await Assert.ThrowsAsync<GithubRemoteException>(() =>
            remote.GetRepositoryAsync(GithubRepositoryReference.Official()));

        Assert.Equal(GithubFailureKind.Network, error.Kind);
        Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
        Assert.Contains("***", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitDetailsPreserveMissingPatchReason()
    {
        var remote = CreateRemote(new StubHandler((_, _) => JsonResponse("""
            {
              "sha": "1234567890abcdef",
              "commit": {
                "message": "title\nbody",
                "author": { "name": "dev", "date": "2026-09-01T00:00:00Z" }
              },
              "parents": [{"sha":"parent"}],
              "stats": {"total": 2, "additions": 2, "deletions": 0},
              "files": [{
                "filename": "binary.dat",
                "status": "modified",
                "additions": 2,
                "deletions": 0,
                "changes": 2
              }]
            }
            """)), null);

        var details = await remote.GetCommitDetailsAsync(GithubRepositoryReference.Official(), "1234567");

        Assert.Equal("title", details.Commit.Title);
        Assert.Single(details.Files);
        Assert.Null(details.Files[0].Patch);
        Assert.Contains("GitHub 未提供", details.Files[0].PatchUnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompareParsesStatusCommitsFilesAndStats()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler((request, _) =>
        {
            captured = CloneRequest(request);
            return JsonResponse("""
                {
                  "status": "behind",
                  "ahead_by": 0,
                  "behind_by": 2,
                  "total_commits": 2,
                  "commits": [
                    {
                      "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "commit": { "message": "first", "author": { "name": "dev", "date": "2026-09-01T00:00:00Z" } },
                      "parents": [{"sha":"base"}]
                    },
                    {
                      "sha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                      "commit": { "message": "second", "author": { "name": "dev", "date": "2026-09-01T01:00:00Z" } },
                      "parents": [{"sha":"aaaa"}]
                    }
                  ],
                  "files": [
                    {
                      "filename": "src/worker.js",
                      "status": "modified",
                      "additions": 2,
                      "deletions": 1,
                      "changes": 3,
                      "patch": "@@ -1,2 +1,3 @@\n context\n-removed\n+added\n+added2"
                    }
                  ],
                  "stats": { "total_commits": 2, "additions": 2, "deletions": 1, "files": 1 }
                }
                """);
        });
        var remote = CreateRemote(handler, null);

        var comparison = await remote.GetCompareAsync(
            GithubRepositoryReference.Official(),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        Assert.NotNull(captured);
        Assert.Contains(
            "/compare/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa...bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            captured!.RequestUri!.AbsolutePath,
            StringComparison.Ordinal);
        Assert.Contains("per_page=250", captured.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal("behind", comparison.Status);
        Assert.Equal(2, comparison.BehindBy);
        Assert.Equal(2, comparison.TotalCommits);
        Assert.Equal(2, comparison.Commits.Count);
        Assert.Equal("bbbbbbb", comparison.Commits[1].ShortSha);
        Assert.Single(comparison.Files);
        Assert.Equal(2, comparison.Additions);
        Assert.Equal(1, comparison.Deletions);
        Assert.False(comparison.CommitsTruncated);
        Assert.False(comparison.FilesTruncated);
        var diff = UnifiedDiffParser.Parse(comparison.Files[0].Patch!);
        Assert.Contains(diff, line => line.Kind == DiffLineKind.Added && line.Text == "added2");
    }

    [Fact]
    public async Task CompareMarksTruncationWhenTotalExceedsReturnedCommits()
    {
        var handler = new StubHandler((_, _) => JsonResponse("""
            {
              "status": "ahead",
              "ahead_by": 250,
              "behind_by": 0,
              "total_commits": 250,
              "commits": [
                {
                  "sha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "commit": { "message": "only", "author": { "name": "dev", "date": "2026-09-01T00:00:00Z" } },
                  "parents": []
                }
              ]
            }
            """));
        var remote = CreateRemote(handler, null);

        var comparison = await remote.GetCompareAsync(GithubRepositoryReference.Official(), "base", "head");

        Assert.True(comparison.CommitsTruncated);
        Assert.Empty(comparison.Files);
    }

    [Fact]
    public async Task CompareWithoutRequiredStatusFailsAsProtocolError()
    {
        var remote = CreateRemote(new StubHandler((_, _) => JsonResponse("""
            { "ahead_by": 0, "behind_by": 0, "total_commits": 0, "commits": [] }
            """)), null);

        var error = await Assert.ThrowsAsync<GithubRemoteException>(() =>
            remote.GetCompareAsync(GithubRepositoryReference.Official(), "base", "head"));

        Assert.Equal(GithubFailureKind.Protocol, error.Kind);
        Assert.Contains("status", error.Message, StringComparison.Ordinal);
    }

    private static GithubCoreRemote CreateRemote(HttpMessageHandler handler, string? token) =>
        new(new HttpClient(handler, disposeHandler: true), new FixedTokenProvider(token));

    private static Task<HttpResponseMessage> JsonResponse(string json, int rateRemaining = 59)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };
        response.Headers.Add("X-RateLimit-Limit", "60");
        response.Headers.Add("X-RateLimit-Remaining", rateRemaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("X-RateLimit-Used", (60 - rateRemaining).ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("X-RateLimit-Reset", "1788230400");
        return Task.FromResult(response);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private sealed class FixedTokenProvider(string? token) : IGithubTokenProvider
    {
        public bool IsConfigured => !string.IsNullOrWhiteSpace(token);
        public string? GetToken() => token;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request, cancellationToken);
    }
}
