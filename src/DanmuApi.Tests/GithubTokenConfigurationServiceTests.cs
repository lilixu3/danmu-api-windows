using DanmuApi.App.Services;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class GithubTokenConfigurationServiceTests
{
    [Fact]
    public async Task SuccessfulValidationSavesTokenThenRefreshesRateLimit()
    {
        var store = new RecordingTokenStore();
        var remote = new TokenRemote
        {
            Login = "octocat",
            RateLimit = new GithubRateLimit(5000, 4999, 1, DateTimeOffset.UtcNow.AddHours(1), true),
        };
        var diagnostics = new RecordingDiagnostics();
        var service = new GithubTokenConfigurationService(store, remote, diagnostics);

        var result = await service.ValidateAndSaveAsync("Bearer valid-secret-token");

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.True(result.Changed);
        Assert.Equal("valid-secret-token", remote.ValidatedToken);
        Assert.Equal("valid-secret-token", store.Token);
        Assert.True(result.State.Configured);
        Assert.DoesNotContain("valid-secret-token", result.Diagnostic, StringComparison.Ordinal);
        Assert.Null(diagnostics.LastDiagnostic);
    }

    [Fact]
    public async Task ValidationFailureDoesNotOverwriteExistingTokenAndRedactsCandidate()
    {
        const string candidate = "invalid-secret-candidate";
        var store = new RecordingTokenStore { Token = "existing-token" };
        var remote = new TokenRemote
        {
            ValidationError = new GithubRemoteException(
                GithubFailureKind.Authentication,
                $"GitHub API 拒绝 {candidate}"),
        };
        var diagnostics = new RecordingDiagnostics();
        var service = new GithubTokenConfigurationService(store, remote, diagnostics);

        var result = await service.ValidateAndSaveAsync(candidate);

        Assert.False(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Equal("existing-token", store.Token);
        Assert.Equal(0, store.SaveCalls);
        Assert.DoesNotContain(candidate, result.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(candidate, diagnostics.LastDiagnostic ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("***", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearRemovesStoredTokenWithoutTreatingEmptyInputAsClear()
    {
        var store = new RecordingTokenStore { Token = "existing-token" };
        var service = new GithubTokenConfigurationService(
            store,
            new TokenRemote(),
            new RecordingDiagnostics());

        var emptyResult = await service.ValidateAndSaveAsync("   ");
        Assert.False(emptyResult.Succeeded);
        Assert.Equal("existing-token", store.Token);
        Assert.Equal(0, store.ClearCalls);

        var clearResult = await service.ClearAsync();
        Assert.True(clearResult.Succeeded);
        Assert.Null(store.Token);
        Assert.Equal(1, store.ClearCalls);
    }

    private sealed class RecordingTokenStore : IGithubTokenStore
    {
        public string? Token { get; set; }
        public int SaveCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public bool IsConfigured => Token is not null;
        public string? GetToken() => Token;
        public void Save(string token)
        {
            SaveCalls++;
            Token = token;
        }
        public void Clear()
        {
            ClearCalls++;
            Token = null;
        }
    }

    private sealed class TokenRemote : IGithubCoreRemote
    {
        public string? Login { get; init; } = "user";
        public GithubRateLimit RateLimit { get; init; } =
            new(60, 59, 1, DateTimeOffset.UtcNow.AddHours(1), true);
        public Exception? ValidationError { get; init; }
        public string? ValidatedToken { get; private set; }
        public GithubRateLimit? LastRateLimit { get; private set; }

        public Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
        {
            ValidatedToken = token;
            return ValidationError is null
                ? Task.FromResult(Login)
                : Task.FromException<string?>(ValidationError);
        }

        public Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default)
        {
            LastRateLimit = RateLimit;
            return Task.FromResult(RateLimit);
        }

        public Task<GithubRepositoryMetadata> GetRepositoryAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(GithubRepositoryReference repository, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommit> GetCommitAsync(GithubRepositoryReference repository, string reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommitPage> GetCommitsAsync(GithubRepositoryReference repository, string reference, int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCommitDetails> GetCommitDetailsAsync(GithubRepositoryReference repository, string sha, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubPullRequestPage> GetPullRequestsAsync(GithubRepositoryReference repository, string baseBranch, string state = "open", int page = 1, int pageSize = 30, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubPullRequest> GetPullRequestAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(GithubRepositoryReference repository, int number, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<GithubCompareResult> GetCompareAsync(GithubRepositoryReference repository, string baseSha, string headSha, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) =>
            LastDiagnostic = error is null ? message : $"{message}: {error.Message}";
    }
}
