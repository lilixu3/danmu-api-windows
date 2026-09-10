using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Avalonia.Media.Imaging;

namespace DanmuApi.App.Services;

/// <summary>
/// 海报图片加载：按 URL 下载并解码为限宽位图，内存缓存按 URL 去重。
/// 失败/超时/非图片返回 null，由调用方显示占位块，不阻塞列表；
/// 失败诊断写入 IAppDiagnostics（只记录 host/状态，不记录完整 URL 或 query）。
/// 语义参考核心前端：封面直接使用核心返回的 imageUrl，referrerpolicy=no-referrer。
/// </summary>
public sealed class PosterImageService
{
    public const int DecodeWidth = 220;
    private const long MaximumBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly IAppDiagnostics? _diagnostics;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new(StringComparer.Ordinal);

    public PosterImageService(HttpClient? httpClient = null, IAppDiagnostics? diagnostics = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _diagnostics = diagnostics;
    }

    public async Task<Bitmap?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var key = url.Trim();
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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            _diagnostics?.Record("海报地址无效：不是 http/https");
            return null;
        }

        var host = uri.Host;
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            {
                _diagnostics?.Record($"海报加载失败：HTTP {(int)response.StatusCode} host={host}");
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaximumBytes)
            {
                _diagnostics?.Record($"海报加载失败：响应过大 host={host}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var buffered = new MemoryStream();
            await stream.CopyToAsync(buffered, timeout.Token).ConfigureAwait(false);
            if (buffered.Length == 0)
            {
                _diagnostics?.Record($"海报加载失败：空响应 host={host}");
                return null;
            }

            if (buffered.Length > MaximumBytes)
            {
                _diagnostics?.Record($"海报加载失败：响应过大 host={host}");
                return null;
            }

            buffered.Position = 0;
            return Bitmap.DecodeToWidth(buffered, DecodeWidth);
        }
        catch (OperationCanceledException)
        {
            _diagnostics?.Record($"海报加载超时：host={host}");
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException
            or ArgumentException)
        {
            _diagnostics?.Record($"海报加载失败：host={host} type={error.GetType().Name}");
            return null;
        }
    }

    private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        UseCookies = false,
    });
}
