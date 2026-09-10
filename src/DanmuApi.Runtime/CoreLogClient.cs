using System.Net;
using System.Text;

namespace DanmuApi.Runtime;

public sealed record CoreLogReadResult(
    bool Succeeded,
    IReadOnlyList<string> Lines,
    string Diagnostic)
{
    public static CoreLogReadResult Success(IReadOnlyList<string> lines) =>
        new(true, lines, $"已读取 {lines.Count} 行核心日志");

    public static CoreLogReadResult Failure(string diagnostic) =>
        new(false, [], diagnostic);
}

public interface ICoreLogClient
{
    Task<CoreLogReadResult> ReadAsync(
        string host,
        int port,
        string? token,
        CancellationToken cancellationToken = default);
}

/// <summary>读取核心 <c>GET /{TOKEN}/api/logs</c> 返回的内存日志快照。</summary>
public sealed class CoreLogClient : ICoreLogClient
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public CoreLogClient(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "核心日志请求超时必须大于零");
        }
    }

    public async Task<CoreLogReadResult> ReadAsync(
        string host,
        int port,
        string? token,
        CancellationToken cancellationToken = default)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        var effectiveToken = string.IsNullOrWhiteSpace(token)
            ? RuntimeDefaults.FallbackToken
            : token.Trim();
        var endpoint = BuildLogsUri(host, port, effectiveToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(
                endpoint,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CoreLogReadResult.Failure("核心日志请求已取消");
        }
        catch (OperationCanceledException)
        {
            return CoreLogReadResult.Failure("核心日志请求超时");
        }
        catch (HttpRequestException error)
        {
            return CoreLogReadResult.Failure($"核心日志请求失败：{Describe(error.Message, effectiveToken)}");
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CoreLogReadResult.Failure("核心日志响应读取已取消");
            }
            catch (OperationCanceledException)
            {
                return CoreLogReadResult.Failure("核心日志响应读取超时");
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                return CoreLogReadResult.Failure($"核心日志响应读取失败：{Describe(error.Message, effectiveToken)}");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return CoreLogReadResult.Failure($"核心日志接口返回 HTTP {(int)response.StatusCode}");
            }

            try
            {
                var text = new UTF8Encoding(false, true).GetString(body);
                var lines = text
                    .Split('\n')
                    .Select(line => line.EndsWith('\r') ? line[..^1] : line)
                    .Where(line => line.Length > 0)
                    .ToArray();
                return CoreLogReadResult.Success(lines);
            }
            catch (DecoderFallbackException error)
            {
                return CoreLogReadResult.Failure($"核心日志响应不是有效 UTF-8：{Describe(error.Message, effectiveToken)}");
            }
        }
    }

    public static Uri BuildLogsUri(string host, int port, string token)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri(
            $"http://{authority}:{port}/{Uri.EscapeDataString(token.Trim())}/api/logs",
            UriKind.Absolute);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new IOException("核心日志响应超过 4MB");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new IOException("核心日志响应超过 4MB");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        UseCookies = false,
    });

    private static string Describe(string message, string token)
    {
        var normalized = string.IsNullOrWhiteSpace(message)
            ? "未提供失败详情"
            : message.Replace('\r', ' ').Replace('\n', ' ');
        return normalized
            .Replace(token, "***", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(token), "***", StringComparison.Ordinal);
    }
}
