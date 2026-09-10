using System.Globalization;
using System.Net;

namespace DanmuApi.Core;

public sealed class GithubRouteException : IOException
{
    public GithubRouteException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class GithubRouteSelectionRequiredException : IOException
{
    public GithubRouteSelectionRequiredException(string message)
        : base(message)
    {
    }
}

public sealed class GithubFileDownloader : IGithubFileDownloader
{
    public const long DefaultMaxBytes = 128L * 1024L * 1024L;
    private readonly HttpClient _httpClient;
    private readonly long _maxBytes;
    private readonly TimeSpan _timeout;
    private readonly IGithubRoutePreferenceStore? _routePreferences;

    public GithubFileDownloader(
        HttpClient httpClient,
        long maxBytes = DefaultMaxBytes,
        TimeSpan? timeout = null,
        IGithubRoutePreferenceStore? routePreferences = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        _maxBytes = maxBytes;
        _timeout = timeout ?? TimeSpan.FromSeconds(150);
        _routePreferences = routePreferences;
    }

    public async Task DownloadAsync(
        Uri originalUri,
        string proxyId,
        string targetPath,
        IProgress<GithubDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var target = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(target) ?? throw new IOException($"下载目标没有父目录：{target}");
        Directory.CreateDirectory(directory);
        var part = target + $".part-{Guid.NewGuid():N}";
        Exception? lastError = null;
        var connectionFailures = 0;
        if (_routePreferences is not null)
        {
            var preference = _routePreferences.Read();
            if (!preference.Confirmed)
            {
                throw new GithubRouteSelectionRequiredException("访问 GitHub 下载前需要先选择线路");
            }

            proxyId = preference.ProxyId;
        }

        var candidates = GithubProxyCatalog.BuildDownloadCandidates(proxyId, originalUri);
        var route = GithubProxyCatalog.GetById(proxyId);

        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(part))
                    {
                        File.Delete(part);
                    }

                    await FetchAsync(candidate, part, route.Label, progress, cancellationToken).ConfigureAwait(false);
                    File.Move(part, target, overwrite: true);
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException or TimeoutException)
                {
                    lastError = error;
                    if (IsConnectionFailure(error))
                    {
                        connectionFailures++;
                    }
                }
            }
        }
        finally
        {
            if (File.Exists(part))
            {
                File.Delete(part);
            }
        }

        var diagnostic = lastError?.Message ?? "未提供失败原因";
        if (connectionFailures == candidates.Count)
        {
            throw new GithubRouteException($"GitHub 下载线路不可达（{route.Label}）：{diagnostic}", lastError);
        }

        throw new IOException($"GitHub 下载失败（{route.Label}）：{diagnostic}", lastError);
    }

    private async Task FetchAsync(
        Uri requestUri,
        string partPath,
        string routeLabel,
        IProgress<GithubDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd("DanmuApiWindows/0.1");
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"下载超过 {_timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} 秒", error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException($"下载返回 HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}");
            }

            var declared = response.Content.Headers.ContentLength;
            if (declared is > DefaultMaxBytes || declared > _maxBytes)
            {
                throw new IOException($"下载响应超过 {_maxBytes.ToString(CultureInfo.InvariantCulture)} 字节上限");
            }

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(
                partPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            long downloaded = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                downloaded = checked(downloaded + read);
                if (downloaded > _maxBytes)
                {
                    throw new IOException($"下载内容超过 {_maxBytes.ToString(CultureInfo.InvariantCulture)} 字节上限");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                progress?.Report(new GithubDownloadProgress(downloaded, declared, routeLabel, requestUri));
            }

            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            if (downloaded == 0)
            {
                throw new IOException("下载响应为空");
            }

            if (declared is not null && declared.Value != downloaded)
            {
                throw new IOException(
                    $"下载长度不完整：期望 {declared.Value.ToString(CultureInfo.InvariantCulture)} 字节，实际 {downloaded.ToString(CultureInfo.InvariantCulture)} 字节");
            }
        }
    }

    private static bool IsConnectionFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or TaskCanceledException)
            {
                return true;
            }

            if (current is HttpRequestException request && request.StatusCode is null)
            {
                return true;
            }

            if (current is System.Net.Sockets.SocketException)
            {
                return true;
            }
        }

        return false;
    }
}
