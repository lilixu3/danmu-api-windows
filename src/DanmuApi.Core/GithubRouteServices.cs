namespace DanmuApi.Core;

public sealed record GithubRoutePreference(string ProxyId, bool Confirmed);

public interface IGithubRoutePreferenceStore
{
    GithubRoutePreference Read();
    void Confirm(string proxyId);
    void Invalidate();
}

public sealed record GithubProxyLatencyResult(
    GithubProxyOption Option,
    TimeSpan? Latency,
    string Diagnostic)
{
    public bool Succeeded => Latency is not null;
}

public interface IGithubProxySpeedTester
{
    Task<IReadOnlyList<GithubProxyLatencyResult>> TestAllAsync(
        IProgress<GithubProxyLatencyResult>? progress = null,
        CancellationToken cancellationToken = default);
}
