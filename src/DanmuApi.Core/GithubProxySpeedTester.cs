using System.Diagnostics;

namespace DanmuApi.Core;

public sealed class GithubProxySpeedTester : IGithubProxySpeedTester
{
    private static readonly Uri ProbeUri = new(GithubProxyCatalog.LatencyProbeUrl, UriKind.Absolute);
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _fastTimeout;
    private readonly TimeSpan _slowTimeout;

    public GithubProxySpeedTester(
        HttpClient httpClient,
        TimeSpan? fastTimeout = null,
        TimeSpan? slowTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _fastTimeout = fastTimeout ?? TimeSpan.FromMilliseconds(3500);
        _slowTimeout = slowTimeout ?? TimeSpan.FromSeconds(7);
        if (_fastTimeout <= TimeSpan.Zero || _slowTimeout <= _fastTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(fastTimeout), "测速超时必须为正，且慢测超时必须大于快测超时");
        }
    }

    public async Task<IReadOnlyList<GithubProxyLatencyResult>> TestAllAsync(
        IProgress<GithubProxyLatencyResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = GithubProxyCatalog.Options
            .Select(option => ProbeOptionAsync(option, progress, cancellationToken))
            .ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<GithubProxyLatencyResult> ProbeOptionAsync(
        GithubProxyOption option,
        IProgress<GithubProxyLatencyResult>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = GithubProxyCatalog.BuildDownloadCandidates(option.Id, ProbeUri);
        var result = await ProbeCandidatesAsync(
            option,
            candidates,
            _fastTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            result = await ProbeCandidatesAsync(
                option,
                candidates.Take(1),
                _slowTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(result);
        return result;
    }

    private async Task<GithubProxyLatencyResult> ProbeCandidatesAsync(
        GithubProxyOption option,
        IEnumerable<Uri> candidates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string diagnostic = "没有测速候选地址";
        foreach (var candidate in candidates.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
            request.Headers.UserAgent.ParseAdd("DanmuApiWindows/0.1");
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 200 and <= 399)
                {
                    await using var body = await response.Content
                        .ReadAsStreamAsync(timeoutSource.Token)
                        .ConfigureAwait(false);
                    _ = await body.ReadAsync(new byte[1], timeoutSource.Token).ConfigureAwait(false);
                    return new GithubProxyLatencyResult(
                        option,
                        Stopwatch.GetElapsedTime(started),
                        "测速成功");
                }

                diagnostic = $"HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                diagnostic = $"超过 {timeout.TotalSeconds:0.#} 秒";
            }
            catch (HttpRequestException error)
            {
                diagnostic = error.Message;
            }
            catch (IOException error)
            {
                diagnostic = error.Message;
            }
        }

        return new GithubProxyLatencyResult(option, null, diagnostic);
    }
}
