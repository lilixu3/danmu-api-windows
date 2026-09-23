using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Avalonia.Media.Imaging;

namespace DanmuApi.App.Services;

/// <summary>
/// Markdown 正文里的图片加载器：只接受 https 绝对地址（与移动端的图片策略一致），
/// 限制单张体积、总张数与超时，失败一律记诊断并返回 null，由渲染层退化成"alt + 链接"。
/// </summary>
public sealed class MarkdownImageLoader : IDisposable
{
    public const int MaxImagesPerDocument = 20;
    private const long MaximumBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Action<string>? _diagnostics;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new(StringComparer.Ordinal);

    public MarkdownImageLoader(HttpClient? httpClient = null, Action<string>? diagnosticSink = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? CreateDefaultClient();
        _diagnostics = diagnosticSink;
    }

    public async Task<Bitmap?> LoadAsync(string? url)
    {
        if (!MarkdownUriPolicy.CanLoadImage(url))
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                _diagnostics?.Invoke($"Markdown 图片地址被拒绝（只允许 https）：{Describe(url!)}");
            }

            return null;
        }

        var key = url!.Trim();
        var load = _cache.GetOrAdd(key, LoadCore);
        var bitmap = await load.ConfigureAwait(false);
        if (bitmap is null)
        {
            _cache.TryRemove(key, out _);
        }

        return bitmap;
    }

    private async Task<Bitmap?> LoadCore(string url)
    {
        var host = new Uri(url).Host;
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                _diagnostics?.Invoke($"Markdown 图片加载失败：HTTP {(int)response.StatusCode} host={host}");
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaximumBytes)
            {
                _diagnostics?.Invoke($"Markdown 图片加载失败：响应过大 host={host}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaximumBytes)
                {
                    _diagnostics?.Invoke($"Markdown 图片加载失败：响应超过 {MaximumBytes / 1024 / 1024}MB host={host}");
                    return null;
                }
            }

            buffer.Position = 0;
            // Bitmap 持有解码结果；流写入的副本可以随 using 释放。
            return new Bitmap(buffer);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ArgumentException)
        {
            // 加载失败不能静默：诊断里留下原因，界面上退化成 alt 文本 + 可点链接。
            _diagnostics?.Invoke($"Markdown 图片加载失败：{error.GetType().Name} host={host}");
            return null;
        }
    }

    private static string Describe(string url)
    {
        var trimmed = url.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120] + "…";
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = Timeout + TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DanmuApiHost/1.0");
        return client;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
