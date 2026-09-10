using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DanmuApi.Runtime;

public sealed class DanmuApiClient : IDanmuApiClient
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;
    private readonly IRuntimeController? _runtimeController;
    private readonly ILocalRequestRecordStore? _requestRecords;

    public DanmuApiClient(
        HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null,
        IRuntimeController? runtimeController = null,
        ILocalRequestRecordStore? requestRecords = null)
    {
        _httpClient = httpClient ?? CreateDefaultClient();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
        if (_requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "弹幕 API 请求超时必须大于零");
        }

        _runtimeController = runtimeController;
        _requestRecords = requestRecords;
    }

    private static readonly IReadOnlyDictionary<string, (HttpMethod Method, string Path, IReadOnlySet<string> Parameters, bool HasBody)> RawApiCatalog =
        new Dictionary<string, (HttpMethod, string, IReadOnlySet<string>, bool)>(StringComparer.Ordinal)
        {
            ["searchAnime"] = (HttpMethod.Get, "/api/v2/search/anime", new HashSet<string>(["keyword"]), false),
            ["searchEpisodes"] = (HttpMethod.Get, "/api/v2/search/episodes", new HashSet<string>(["anime", "episode"]), false),
            ["matchAnime"] = (HttpMethod.Post, "/api/v2/match", new HashSet<string>(["fileName"]), true),
            ["getBangumi"] = (HttpMethod.Get, "/api/v2/bangumi/:animeId", new HashSet<string>(["animeId"]), false),
            ["getComment"] = (HttpMethod.Get, "/api/v2/comment/:commentId", new HashSet<string>(["commentId", "format", "duration", "segmentflag"]), false),
            ["getCommentByUrl"] = (HttpMethod.Get, "/api/v2/comment", new HashSet<string>(["url", "format", "duration", "segmentflag"]), false),
            ["getSegmentComment"] = (HttpMethod.Post, "/api/v2/segmentcomment", new HashSet<string>(["format"]), true),
            ["fongmiGet"] = (HttpMethod.Get, "/api/v2/fongmi/danmaku", new HashSet<string>(["name", "episode"]), false),
            ["fongmiPost"] = (HttpMethod.Post, "/api/v2/fongmi/danmaku", new HashSet<string>(["name", "episode"]), true),
            ["danmakuGet"] = (HttpMethod.Get, "/danmaku", new HashSet<string>(["name", "episode"]), false),
            ["danmakuPost"] = (HttpMethod.Post, "/danmaku", new HashSet<string>(["name", "episode"]), true),
        };

    public async Task<DanmuRawApiResponse> SendRawAsync(
        string host,
        int port,
        string? token,
        string apiKey,
        IReadOnlyDictionary<string, string?> parameters,
        string? jsonBody = null,
        CancellationToken cancellationToken = default)
    {
        if (!RawApiCatalog.TryGetValue(apiKey, out var definition))
        {
            throw new ArgumentException($"未允许的接口：{apiKey}", nameof(apiKey));
        }

        var query = new List<string>();
        var path = definition.Path;
        foreach (var pair in parameters)
        {
            if (!definition.Parameters.Contains(pair.Key))
            {
                throw new ArgumentException($"接口 {apiKey} 不接受参数 {pair.Key}", nameof(parameters));
            }

            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            if (path.Contains($":{pair.Key}", StringComparison.Ordinal))
            {
                path = path.Replace($":{pair.Key}", Uri.EscapeDataString(pair.Value.Trim()), StringComparison.Ordinal);
            }
            else if (!(definition.HasBody && ((apiKey == "matchAnime" && pair.Key == "fileName") ||
                apiKey is "fongmiPost" or "danmakuPost")))
            {
                query.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value.Trim())}");
            }
        }

        if (path.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException($"接口 {apiKey} 缺少路径参数", nameof(parameters));
        }

        if (query.Count > 0)
        {
            path += "?" + string.Join('&', query);
        }

        if (definition.HasBody && jsonBody is null)
        {
            throw new ArgumentException($"接口 {apiKey} 缺少 JSON 请求体", nameof(jsonBody));
        }

        var endpoint = BuildApiUri(host, port, EffectiveToken(token), path);
        var content = jsonBody is null ? null : new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var started = Stopwatch.GetTimestamp();
        var response = await SendRawHttpAsync(host, port, token, null, apiKey, definition.Method, endpoint, content, cancellationToken).ConfigureAwait(false);
        return response with { Duration = Stopwatch.GetElapsedTime(started), RequestPath = path };
    }

    public async Task<DanmuDownloadPayload> DownloadCommentAsync(
        string host,
        int port,
        string? token,
        long episodeId,
        string formatValue,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (episodeId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(episodeId), "弹幕 ID 不能为负数");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(formatValue);
        var effectiveTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        var path = $"/api/v2/comment/{episodeId.ToString(CultureInfo.InvariantCulture)}?format={Uri.EscapeDataString(formatValue.Trim())}";
        var endpoint = BuildApiUri(host, port, EffectiveToken(token), path);
        var trace = BeginTrace("downloadComment", HttpMethod.Get, endpoint, null);
        var effectiveToken = EffectiveToken(token);
        if (_runtimeController is not null &&
            (_runtimeController.Snapshot.State != DesktopRuntimeState.Running ||
             _runtimeController.Snapshot.Port != port))
        {
            var notRunning = new DanmuApiException(DanmuApiFailureKind.ServiceNotRunning, "服务未运行，无法调用弹幕 API");
            CompleteTrace(trace, null, false, FailureLabel(notRunning));
            throw notRunning;
        }

        const int MaximumDownloadBytes = 32 * 1024 * 1024;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(effectiveTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                byte[] errorBody;
                try
                {
                    errorBody = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception readError) when (readError is IOException or OperationCanceledException)
                {
                    errorBody = [];
                }

                var responseMessage = TryReadErrorMessage(errorBody);
                var diagnostic = string.IsNullOrWhiteSpace(responseMessage)
                    ? $"核心弹幕 API 返回 HTTP {(int)response.StatusCode}"
                    : $"核心弹幕 API 返回 HTTP {(int)response.StatusCode}：{responseMessage}";
                var kind = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? DanmuApiFailureKind.Authentication
                    : response.StatusCode == HttpStatusCode.NotFound
                        ? DanmuApiFailureKind.NotFound
                        : DanmuApiFailureKind.Http;
                var failure = new DanmuApiException(kind, Redact(diagnostic, token, null), statusCode: (int)response.StatusCode);
                CompleteTrace(trace, (int)response.StatusCode, false, FailureLabel(failure));
                throw failure;
            }

            var body = await ReadBoundedAsync(response.Content, timeout.Token, MaximumDownloadBytes).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            string? danmuFormat = null;
            int? danmuCount = null;
            if (response.Headers.TryGetValues("X-Danmu-Format", out var formatValues))
            {
                danmuFormat = formatValues.FirstOrDefault();
            }

            if (response.Headers.TryGetValues("X-Danmu-Count", out var countValues) &&
                int.TryParse(countValues.FirstOrDefault()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCount) &&
                parsedCount >= 0)
            {
                danmuCount = parsedCount;
            }

            CompleteTrace(trace, (int)response.StatusCode, true, null);
            return new DanmuDownloadPayload((int)response.StatusCode, contentType, body, danmuFormat, danmuCount);
        }
        catch (DanmuApiException)
        {
            throw;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Cancelled, "弹幕 API 请求已取消", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (OperationCanceledException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Timeout, "弹幕 API 请求超时", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (HttpRequestException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Connection, $"弹幕 API 连接失败：{Redact(error.Message, token, null)}", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (IOException error) when (error.Message.Contains("超过", StringComparison.Ordinal))
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.ResponseTooLarge, "弹幕 API 响应超过限制", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (IOException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Connection, $"弹幕 API 响应读取失败：{Redact(error.Message, token, null)}", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
    }

    public Task<DanmuSearchAnimeResult> SearchAnimeAsync(
        string host,
        int port,
        string? token,
        string keyword,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(
            host,
            port,
            token,
            "searchAnime",
            HttpMethod.Get,
            BuildApiUri(host, port, EffectiveToken(token), $"/api/v2/search/anime?keyword={EncodeRequired(keyword)}"),
            ParseSearchAnime,
            cancellationToken);

    public Task<DanmuSearchEpisodesResult> SearchEpisodesAsync(
        string host,
        int port,
        string? token,
        string anime,
        string? episode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anime);
        var query = $"anime={EncodeRequired(anime)}";
        if (!string.IsNullOrWhiteSpace(episode))
        {
            query += $"&episode={EncodeRequired(episode)}";
        }

        return SendJsonAsync(
            host,
            port,
            token,
            "searchEpisodes",
            HttpMethod.Get,
            BuildApiUri(host, port, EffectiveToken(token), $"/api/v2/search/episodes?{query}"),
            ParseSearchEpisodes,
            cancellationToken);
    }

    public Task<DanmuMatchResult> MatchAsync(
        string host,
        int port,
        string? token,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var body = BuildMatchPayload(fileName);
        return SendJsonAsync(
            host,
            port,
            token,
            "matchAnime",
            HttpMethod.Post,
            BuildApiUri(host, port, EffectiveToken(token), "/api/v2/match"),
            ParseMatch,
            cancellationToken,
            body);
    }

    public Task<DanmuBangumiResult> GetBangumiAsync(
        string host,
        int port,
        string? token,
        int animeId,
        CancellationToken cancellationToken = default)
    {
        if (animeId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(animeId), "动漫 ID 不能为负数");
        }

        return SendJsonAsync(
            host,
            port,
            token,
            "getBangumi",
            HttpMethod.Get,
            BuildApiUri(host, port, EffectiveToken(token), $"/api/v2/bangumi/{animeId.ToString(CultureInfo.InvariantCulture)}"),
            ParseBangumi,
            cancellationToken);
    }

    public Task<DanmuResult> GetCommentAsync(
        string host,
        int port,
        string? token,
        int commentId,
        bool includeDuration = true,
        string format = "json",
        CancellationToken cancellationToken = default,
        bool segmentFlag = false)
    {
        if (commentId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commentId), "弹幕 ID 不能为负数");
        }

        var normalizedFormat = ValidateFormat(format);
        var path = $"/api/v2/comment/{commentId.ToString(CultureInfo.InvariantCulture)}?format={normalizedFormat}&duration={(includeDuration ? "true" : "false")}&segmentflag={(segmentFlag ? "true" : "false")}";
        return SendCommentAsync(
            host,
            port,
            token,
            "getComment",
            BuildApiUri(host, port, EffectiveToken(token), path),
            normalizedFormat,
            cancellationToken);
    }

    public Task<DanmuResult> GetCommentByUrlAsync(
        string host,
        int port,
        string? token,
        string videoUrl,
        bool includeDuration = true,
        string format = "json",
        CancellationToken cancellationToken = default,
        bool segmentFlag = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoUrl);
        var normalizedFormat = ValidateFormat(format);
        var path = $"/api/v2/comment?url={EncodeRequired(videoUrl)}&format={normalizedFormat}&duration={(includeDuration ? "true" : "false")}&segmentflag={(segmentFlag ? "true" : "false")}";
        return SendCommentAsync(
            host,
            port,
            token,
            "getCommentByUrl",
            BuildApiUri(host, port, EffectiveToken(token), path),
            normalizedFormat,
            cancellationToken);
    }

    public Task<DanmuResult> GetSegmentCommentAsync(
        string host,
        int port,
        string? token,
        JsonElement segment,
        string format = "json",
        CancellationToken cancellationToken = default)
    {
        if (segment.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("分片请求体必须是 JSON 对象", nameof(segment));
        }

        var normalizedFormat = ValidateFormat(format);
        var body = segment.GetRawText();
        return SendJsonAsync(
            host,
            port,
            token,
            "getSegmentComment",
            HttpMethod.Post,
            BuildApiUri(host, port, EffectiveToken(token), $"/api/v2/segmentcomment?format={normalizedFormat}"),
            ParseCommentJson,
            cancellationToken,
            body);
    }

    public Task<DanmuFavoriteListResult> GetFavoritesAsync(
        string host,
        int port,
        string? token,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(
            host,
            port,
            token,
            "getFavorites",
            HttpMethod.Get,
            BuildApiUri(host, port, EffectiveToken(token), "/api/v2/favorite/list"),
            ParseFavorites,
            cancellationToken);

    public Task<string> AddFavoriteAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string keyword,
        CancellationToken cancellationToken = default) =>
        SendFavoriteMutationAsync(host, port, token, adminToken, "/api/v2/favorite/add", keyword, null, cancellationToken);

    public Task<string> RemoveFavoriteAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string keyword,
        CancellationToken cancellationToken = default) =>
        SendFavoriteMutationAsync(host, port, token, adminToken, "/api/v2/favorite/remove", keyword, null, cancellationToken);

    public Task<string> RefreshFavoriteAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string keyword,
        CancellationToken cancellationToken = default) =>
        SendFavoriteMutationAsync(host, port, token, adminToken, "/api/v2/favorite/refresh", keyword, null, cancellationToken);

    public Task<string> SetFavoriteScheduleAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string keyword,
        DanmuFavoriteSchedule? schedule,
        CancellationToken cancellationToken = default) =>
        SendFavoriteMutationAsync(host, port, token, adminToken, "/api/v2/favorite/schedule", keyword, schedule, cancellationToken);

    public static Uri BuildApiUri(string host, int port, string? token, string pathAndQuery)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        if (!pathAndQuery.StartsWith('/'))
        {
            throw new ArgumentException("核心 API 路径必须以 / 开头", nameof(pathAndQuery));
        }

        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;
        return new Uri($"http://{authority}:{port}/{Uri.EscapeDataString(token.Trim())}{pathAndQuery}", UriKind.Absolute);
    }

    public static DanmuSearchAnimeResult ParseSearchAnime(byte[] body)
    {
        using var document = ParseDocument(body, "搜索动漫");
        var root = RequireObject(document.RootElement, "根对象");
        var success = RequireBoolean(root, "success");
        if (!success)
        {
            return new(false, [], OptionalMessage(root));
        }

        var animes = RequireArray(root, "animes").EnumerateArray().Select(ParseAnime).ToArray();
        return new(true, animes);
    }

    public static DanmuSearchEpisodesResult ParseSearchEpisodes(byte[] body)
    {
        using var document = ParseDocument(body, "搜索剧集");
        var root = RequireObject(document.RootElement, "根对象");
        var success = RequireBoolean(root, "success");
        if (!success)
        {
            return new(false, [], OptionalMessage(root));
        }

        var animes = RequireArray(root, "animes")
            .EnumerateArray()
            .Select(ParseSearchEpisodesAnime)
            .ToArray();
        return new(true, animes, OptionalMessage(root));
    }

    public static DanmuMatchResult ParseMatch(byte[] body)
    {
        using var document = ParseDocument(body, "自动匹配");
        var root = RequireObject(document.RootElement, "根对象");
        var matched = RequireBoolean(root, "isMatched");
        var matches = root.TryGetProperty("matches", out var matchesElement)
            ? RequireArrayValue(matchesElement, "matches").EnumerateArray().Select(ParseMatchItem).ToArray()
            : [];
        if (!matched && matches.Length != 0)
        {
            throw Protocol("isMatched=false 时 matches 必须为空");
        }

        return new(matched, matches, OptionalMessage(root));
    }

    public static DanmuBangumiResult ParseBangumi(byte[] body)
    {
        using var document = ParseDocument(body, "番剧详情");
        var root = RequireObject(document.RootElement, "根对象");
        var success = RequireBoolean(root, "success");
        if (!success)
        {
            return new(false, null, OptionalMessage(root));
        }

        if (!root.TryGetProperty("bangumi", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw Protocol("成功响应缺少 bangumi 对象");
        }

        return new(true, ParseBangumiObject(RequireObject(value, "bangumi")));
    }

    public static DanmuFavoriteListResult ParseFavorites(byte[] body)
    {
        using var document = ParseDocument(body, "收藏列表");
        var root = RequireObject(document.RootElement, "根对象");
        var success = RequireBoolean(root, "success");
        if (!success)
        {
            return new(false, new(false, false, OptionalMessage(root)), [], OptionalMessage(root));
        }

        var capabilities = new DanmuFavoriteCapabilities(
            OptionalBoolean(root, "favoriteSupported") ?? false,
            OptionalBoolean(root, "scheduledRefreshSupported") ?? false,
            OptionalString(root, "favoriteSupportMessage"));
        var favorites = RequireArray(root, "favorites").EnumerateArray().Select(ParseFavorite).ToArray();
        return new(true, capabilities, favorites);
    }

    public static DanmuResult ParseCommentJson(byte[] body)
    {
        using var document = ParseDocument(body, "弹幕");
        var root = RequireObject(document.RootElement, "根对象");
        var comments = RequireArray(root, "comments").EnumerateArray().Select(ParseComment).ToArray();
        var count = root.TryGetProperty("count", out var countElement)
            ? RequireNonNegativeInt32(countElement, "count")
            : comments.Length;
        var duration = OptionalNumber(root, "videoDuration", nonNegative: true);
        return new(
            count,
            comments,
            duration,
            "application/json");
    }

    public static DanmuResult ParseCommentXml(byte[] body)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(body);
            var xml = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            var comments = xml.Descendants("d").Select(element =>
            {
                var position = element.Attribute("p")?.Value;
                if (string.IsNullOrWhiteSpace(position))
                {
                    throw Protocol("XML 弹幕缺少 p 属性");
                }

                return new DanmuComment(
                    element.Value,
                    ParseDoublePosition(position, 0),
                    ParseIntegerPosition(position, 1),
                    ParseColorPosition(position),
                    position,
                    Source: ParseSource(position));
            }).ToArray();
            return new(comments.Length, comments, null, "application/xml");
        }
        catch (DecoderFallbackException error)
        {
            throw new DanmuApiException(DanmuApiFailureKind.Encoding, "弹幕 XML 不是有效 UTF-8", error);
        }
        catch (DanmuApiException)
        {
            throw;
        }
        catch (Exception error) when (error is XmlException or FormatException)
        {
            throw new DanmuApiException(DanmuApiFailureKind.Protocol, "弹幕 XML 格式无效", error);
        }
    }

    public static DanmuResult ParseSegmentCommentJson(byte[] body) => ParseCommentJson(body);

    private async Task<T> SendJsonAsync<T>(
        string host,
        int port,
        string? token,
        string scene,
        HttpMethod method,
        Uri endpoint,
        Func<byte[], T> parser,
        CancellationToken cancellationToken,
        string? jsonBody = null)
    {
        var trace = BeginTrace(scene, method, endpoint, jsonBody);
        var body = jsonBody is null ? null : new StringContent(jsonBody, Encoding.UTF8, "application/json");
        var effectiveToken = EffectiveToken(token);
        var response = await SendAsync(host, port, effectiveToken, null, method, endpoint, body, trace, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = parser(response.Bytes);
            CompleteTrace(trace, response.StatusCode, true, null, response.Bytes, response.ContentType);
            return result;
        }
        catch (DanmuApiException error)
        {
            CompleteTrace(trace, response.StatusCode, false, FailureLabel(error), response.Bytes, response.ContentType);
            throw;
        }
        catch (JsonException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Protocol, "核心 API 返回的 JSON 无效", error);
            CompleteTrace(trace, response.StatusCode, false, FailureLabel(failure), response.Bytes, response.ContentType);
            throw failure;
        }
        catch (InvalidDataException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Protocol, error.Message, error);
            CompleteTrace(trace, response.StatusCode, false, FailureLabel(failure), response.Bytes, response.ContentType);
            throw failure;
        }
    }

    private async Task<DanmuResult> SendCommentAsync(
        string host,
        int port,
        string? token,
        string scene,
        Uri endpoint,
        string format,
        CancellationToken cancellationToken)
    {
        var trace = BeginTrace(scene, HttpMethod.Get, endpoint, null);
        var response = await SendAsync(host, port, token, null, HttpMethod.Get, endpoint, null, trace, cancellationToken).ConfigureAwait(false);
        try
        {
            var result = format == "xml" ? ParseCommentXml(response.Bytes) : ParseCommentJson(response.Bytes);
            CompleteTrace(trace, response.StatusCode, true, null, response.Bytes, response.ContentType);
            return result;
        }
        catch (DanmuApiException error)
        {
            CompleteTrace(trace, response.StatusCode, false, FailureLabel(error), response.Bytes, response.ContentType);
            throw;
        }
    }

    private async Task<string> SendFavoriteMutationAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string path,
        string keyword,
        DanmuFavoriteSchedule? schedule,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        var payload = BuildFavoritePayload(keyword, schedule);
        var effectivePathToken = string.IsNullOrWhiteSpace(adminToken) ? token : adminToken;
        var endpoint = BuildApiUri(host, port, EffectiveToken(effectivePathToken), path);
        var trace = BeginTrace(FavoriteScene(path), HttpMethod.Post, endpoint, payload);
        var body = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await SendAsync(host, port, token, adminToken, HttpMethod.Post, endpoint, body, trace, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = ParseDocument(response.Bytes, "收藏操作");
            var root = RequireObject(document.RootElement, "根对象");
            if (!RequireBoolean(root, "success"))
            {
                throw new DanmuApiException(DanmuApiFailureKind.Protocol, Redact(OptionalMessage(root) ?? "收藏操作失败", token, adminToken));
            }

            CompleteTrace(trace, response.StatusCode, true, null, response.Bytes, response.ContentType);
            return Redact(OptionalMessage(root) ?? "收藏操作完成", token, adminToken);
        }
        catch (DanmuApiException error)
        {
            CompleteTrace(trace, response.StatusCode, false, FailureLabel(error), response.Bytes, response.ContentType);
            throw;
        }
        catch (JsonException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Protocol, "收藏接口返回的 JSON 无效", error);
            CompleteTrace(trace, response.StatusCode, false, FailureLabel(failure), response.Bytes, response.ContentType);
            throw failure;
        }
    }

    private async Task<DanmuRawApiResponse> SendRawHttpAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        string scene,
        HttpMethod method,
        Uri endpoint,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        var trace = BeginTrace($"接口调试/{scene}", method, endpoint, await ReadTraceContentAsync(content).ConfigureAwait(false));
        var effectiveToken = EffectiveToken(token);
        if (_runtimeController is not null &&
            (_runtimeController.Snapshot.State != DesktopRuntimeState.Running ||
             _runtimeController.Snapshot.Port != port))
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.ServiceNotRunning, "服务未运行，无法调用弹幕 API");
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            string? bodyText = null;
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase) ||
                contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    bodyText = new UTF8Encoding(false, true).GetString(bytes);
                }
                catch (DecoderFallbackException error)
                {
                    throw new DanmuApiException(DanmuApiFailureKind.Encoding, "核心 API 响应不是有效 UTF-8", error, (int)response.StatusCode);
                }
            }

            var succeeded = response.IsSuccessStatusCode;
            var errorMessage = succeeded ? null : $"HTTP {(int)response.StatusCode}";
            CompleteTrace(trace, (int)response.StatusCode, succeeded, errorMessage, bytes, contentType);
            return new DanmuRawApiResponse((int)response.StatusCode, contentType, bytes, bodyText, endpoint.PathAndQuery, TimeSpan.Zero);
        }
        catch (DanmuApiException error)
        {
            CompleteTrace(trace, error.StatusCode, false, FailureLabel(error));
            throw;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Cancelled, "弹幕 API 请求已取消", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (OperationCanceledException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Timeout, "弹幕 API 请求超时", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (HttpRequestException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Connection, $"弹幕 API 连接失败：{Redact(error.Message, token, adminToken)}", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (IOException error) when (error.Message.Contains("超过", StringComparison.Ordinal))
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.ResponseTooLarge, "弹幕 API 响应超过 8MB", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (IOException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Connection, $"弹幕 API 响应读取失败：{Redact(error.Message, token, adminToken)}", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
    }

    private async Task<ResponsePayload> SendAsync(
        string host,
        int port,
        string? token,
        string? adminToken,
        HttpMethod method,
        Uri endpoint,
        HttpContent? content,
        RequestTrace? trace,
        CancellationToken cancellationToken)
    {
        RuntimeValidation.ValidateHost(host);
        RuntimeValidation.ValidatePort(port);
        var effectiveToken = EffectiveToken(token);
        if (_runtimeController is not null &&
            (_runtimeController.Snapshot.State != DesktopRuntimeState.Running ||
             _runtimeController.Snapshot.Port != port))
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.ServiceNotRunning, "服务未运行，无法调用弹幕 API");
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        using var request = new HttpRequestMessage(method, endpoint) { Content = content };
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (!response.IsSuccessStatusCode)
            {
                var kind = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? DanmuApiFailureKind.Authentication
                    : response.StatusCode == HttpStatusCode.NotFound
                        ? DanmuApiFailureKind.NotFound
                        : DanmuApiFailureKind.Http;
                var responseMessage = TryReadErrorMessage(bytes);
                var diagnostic = string.IsNullOrWhiteSpace(responseMessage)
                    ? $"核心弹幕 API 返回 HTTP {(int)response.StatusCode}"
                    : $"核心弹幕 API 返回 HTTP {(int)response.StatusCode}：{responseMessage}";
                var failure = new DanmuApiException(
                    kind,
                    Redact(diagnostic, token, adminToken),
                    statusCode: (int)response.StatusCode);
                CompleteTrace(trace, (int)response.StatusCode, false, FailureLabel(failure), bytes, contentType);
                throw failure;
            }

            return new ResponsePayload(bytes, (int)response.StatusCode, contentType);
        }
        catch (DanmuApiException)
        {
            throw;
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Cancelled, "弹幕 API 请求已取消", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (OperationCanceledException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Timeout, "弹幕 API 请求超时", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (HttpRequestException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Connection, $"弹幕 API 连接失败：{Redact(error.Message, token, adminToken)}", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (IOException error) when (error.Message.Contains("超过", StringComparison.Ordinal))
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.ResponseTooLarge, "弹幕 API 响应超过 8MB", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
        catch (IOException error)
        {
            var failure = new DanmuApiException(DanmuApiFailureKind.Connection, $"弹幕 API 响应读取失败：{Redact(error.Message, token, adminToken)}", error);
            CompleteTrace(trace, null, false, FailureLabel(failure));
            throw failure;
        }
    }

    private static async Task<string?> ReadTraceContentAsync(HttpContent? content) =>
        content is null ? null : await content.ReadAsStringAsync().ConfigureAwait(false);

    private RequestTrace? BeginTrace(string scene, HttpMethod method, Uri endpoint, string? jsonBody)
    {
        if (_requestRecords is null)
        {
            return null;
        }

        return new RequestTrace(
            _requestRecords.AllocateId(),
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp(),
            DanmuRequestSceneContext.Current ?? scene,
            method.Method,
            RequestRecordRedactor.MaskInterface(endpoint),
            RequestRecordRedactor.MaskJsonValues(jsonBody));
    }

    private void CompleteTrace(
        RequestTrace? trace,
        int? statusCode,
        bool success,
        string? errorMessage,
        ReadOnlySpan<byte> responseBody = default,
        string contentType = "application/octet-stream")
    {
        if (trace is null || _requestRecords is null)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(trace.StartedTimestamp);
        _requestRecords.Add(new LocalRequestRecord(
            trace.Id,
            trace.Timestamp,
            trace.Scene,
            trace.Method,
            trace.Interface,
            trace.Parameters,
            statusCode,
            Math.Max(0, (long)Math.Round(elapsed.TotalMilliseconds)),
            success,
            errorMessage,
            responseBody.IsEmpty ? string.Empty : RequestRecordRedactor.MaskResponseSummary(responseBody, contentType)));
    }

    private static string FavoriteScene(string path) => path switch
    {
        "/api/v2/favorite/add" => "收藏/添加",
        "/api/v2/favorite/remove" => "收藏/删除",
        "/api/v2/favorite/refresh" => "收藏/刷新",
        "/api/v2/favorite/schedule" => "收藏/定时刷新",
        _ => "收藏操作",
    };

    private static string FailureLabel(DanmuApiException error) => error.Kind switch
    {
        DanmuApiFailureKind.ServiceNotRunning => "服务未运行",
        DanmuApiFailureKind.Cancelled => "请求已取消",
        DanmuApiFailureKind.Timeout => "请求超时",
        DanmuApiFailureKind.Connection => "连接失败",
        DanmuApiFailureKind.Authentication => "认证失败",
        DanmuApiFailureKind.NotFound => "接口或资源不存在",
        DanmuApiFailureKind.Http => error.StatusCode is int status ? $"HTTP {status}" : "HTTP 请求失败",
        DanmuApiFailureKind.Protocol => "响应协议无效",
        DanmuApiFailureKind.ResponseTooLarge => "响应超过 8MB",
        DanmuApiFailureKind.Encoding => "响应编码无效",
        _ => "请求失败",
    };

    private sealed record RequestTrace(
        long Id,
        DateTimeOffset Timestamp,
        long StartedTimestamp,
        string Scene,
        string Method,
        string Interface,
        string Parameters);

    private sealed record ResponsePayload(byte[] Bytes, int StatusCode, string ContentType);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken, int maximumBytes = MaxResponseBytes)
    {
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is not null && declaredLength > maximumBytes)
        {
            throw new IOException($"弹幕 API 响应超过 {(int)Math.Round(maximumBytes / 1024.0 / 1024.0)}MB");
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

            if (buffer.Length + read > maximumBytes)
            {
                throw new IOException($"弹幕 API 响应超过 {(int)Math.Round(maximumBytes / 1024.0 / 1024.0)}MB");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static string? TryReadErrorMessage(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? OptionalString(document.RootElement, "errorMessage") ?? OptionalString(document.RootElement, "message")
                : null;
        }
        catch (Exception error) when (error is JsonException or DanmuApiException)
        {
            return null;
        }
    }

    private static DanmuAnime ParseAnime(JsonElement value)
    {
        var root = RequireObject(value, "anime");
        var links = root.TryGetProperty("links", out var linksElement)
            ? RequireArrayValue(linksElement, "links").EnumerateArray().Select(ParseLink).ToArray()
            : [];
        return new(
            RequireInt32(root, "animeId", true),
            OptionalString(root, "bangumiId") ?? string.Empty,
            RequireString(root, "animeTitle"),
            OptionalString(root, "type") ?? string.Empty,
            OptionalString(root, "typeDescription") ?? string.Empty,
            OptionalString(root, "imageUrl") ?? string.Empty,
            OptionalString(root, "startDate") ?? string.Empty,
            OptionalInt32(root, "episodeCount", true) ?? 0,
            OptionalNumber(root, "rating", true) ?? 0,
            OptionalBoolean(root, "isFavorited") ?? false,
            OptionalString(root, "source") ?? string.Empty,
            links);
    }

    private static DanmuLink ParseLink(JsonElement value)
    {
        var root = RequireObject(value, "link");
        return new(
            OptionalString(root, "name") ?? string.Empty,
            OptionalString(root, "url") ?? string.Empty,
            OptionalString(root, "title") ?? string.Empty,
            OptionalInt32(root, "id", true) ?? 0);
    }

    private static DanmuAnimeMatch ParseMatchItem(JsonElement value)
    {
        var root = RequireObject(value, "match");
        return new(
            RequireInt32(root, "episodeId", true),
            RequireInt32(root, "animeId", true),
            RequireString(root, "animeTitle"),
            RequireString(root, "episodeTitle"),
            OptionalString(root, "type") ?? string.Empty,
            OptionalString(root, "typeDescription") ?? string.Empty,
            OptionalNumber(root, "shift", false) ?? 0,
            OptionalString(root, "imageUrl") ?? string.Empty,
            OptionalString(root, "url") ?? string.Empty);
    }

    private static DanmuSearchEpisodesAnime ParseSearchEpisodesAnime(JsonElement value)
    {
        var root = RequireObject(value, "搜索剧集动漫");
        return new(
            RequireInt32(root, "animeId", true),
            RequireString(root, "animeTitle"),
            OptionalString(root, "type") ?? string.Empty,
            OptionalString(root, "typeDescription") ?? string.Empty,
            RequireArray(root, "episodes")
                .EnumerateArray()
                .Select(ParseSearchEpisode)
                .ToArray());
    }

    private static DanmuSearchEpisode ParseSearchEpisode(JsonElement value)
    {
        var root = RequireObject(value, "搜索剧集集");
        return new(
            RequireInt32(root, "episodeId", true),
            RequireString(root, "episodeTitle"),
            OptionalString(root, "url") ?? string.Empty);
    }

    private static DanmuEpisode ParseEpisode(JsonElement value)
    {
        var root = RequireObject(value, "episode");
        return new(
            OptionalString(root, "seasonId") ?? string.Empty,
            RequireInt32(root, "episodeId", true),
            RequireString(root, "episodeTitle"),
            RequireString(root, "episodeNumber"),
            OptionalString(root, "airDate") ?? string.Empty,
            OptionalString(root, "url") ?? string.Empty);
    }

    private static DanmuBangumi ParseBangumiObject(JsonElement root) => new(
        RequireInt32(root, "animeId", true),
        OptionalString(root, "bangumiId") ?? string.Empty,
        RequireString(root, "animeTitle"),
        OptionalString(root, "imageUrl") ?? string.Empty,
        OptionalBoolean(root, "isOnAir") ?? false,
        OptionalInt32(root, "airDay", true) ?? 0,
        OptionalBoolean(root, "isFavorited") ?? false,
        OptionalNumber(root, "rating", true) ?? 0,
        OptionalString(root, "type") ?? string.Empty,
        OptionalString(root, "typeDescription") ?? string.Empty,
        root.TryGetProperty("seasons", out var seasons)
            ? RequireArrayValue(seasons, "seasons").EnumerateArray().Select(ParseSeason).ToArray()
            : [],
        RequireArray(root, "episodes").EnumerateArray().Select(ParseEpisode).ToArray());

    private static DanmuSeason ParseSeason(JsonElement value)
    {
        var root = RequireObject(value, "season");
        return new(
            RequireString(root, "id"),
            OptionalString(root, "airDate") ?? string.Empty,
            RequireString(root, "name"),
            OptionalInt32(root, "episodeCount", true) ?? 0);
    }

    private static DanmuComment ParseComment(JsonElement value)
    {
        var root = RequireObject(value, "comment");
        var rawPosition = OptionalString(root, "p");
        var time = OptionalNumber(root, "t", true) ?? (rawPosition is null ? 0 : ParseDoublePosition(rawPosition, 0));
        var mode = rawPosition is null ? 1 : ParseIntegerPosition(rawPosition, 1);
        var color = rawPosition is null ? 16_777_215 : ParseColorPosition(rawPosition);
        var text = RequireString(root, "m");
        var source = rawPosition is null ? null : ParseSource(rawPosition);
        var cid = OptionalInt64(root, "cid", nonNegative: true);
        var like = OptionalInt64(root, "like", nonNegative: true);
        var colorV2 = OptionalString(root, "color_v2");
        var explicitSource = OptionalString(root, "source");
        return new(text, time, mode, color, rawPosition, root.Clone(), explicitSource ?? source, cid, like, colorV2);
    }

    private static string? ParseSource(string position)
    {
        var value = position[(position.LastIndexOf(',') + 1)..].Trim();
        return value.StartsWith('[') && value.EndsWith(']') && value.Length > 2
            ? value[1..^1]
            : null;
    }

    private static DanmuSegment ParseSegmentItem(JsonElement value)
    {
        var root = RequireObject(value, "segment");
        return new(
            RequireString(root, "type"),
            RequireNumber(root, "segment_start", true),
            RequireNumber(root, "segment_end", true),
            RequireString(root, "url"),
            OptionalString(root, "data"));
    }

    private static DanmuFavoriteItem ParseFavorite(JsonElement value)
    {
        var root = RequireObject(value, "favorite");
        return new(
            RequireString(root, "keyword"),
            OptionalString(root, "animeTitle") ?? string.Empty,
            OptionalString(root, "source") ?? string.Empty,
            root.TryGetProperty("sources", out var sources)
                ? RequireArrayValue(sources, "sources").EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw Protocol("sources 必须是字符串数组")).ToArray()
                : [],
            OptionalString(root, "imageUrl") ?? string.Empty,
            OptionalInt32(root, "episodeCount", true) ?? 0,
            OptionalInt32(root, "resultsCount", true) ?? 0,
            RequireInt64(root, "timestamp", nonNegative: true),
            RequireInt64(root, "lastRefreshAt", nonNegative: true),
            root.TryGetProperty("refreshSchedule", out var schedule) && schedule.ValueKind != JsonValueKind.Null
                ? ParseSchedule(RequireObject(schedule, "refreshSchedule"))
                : null);
    }

    private static DanmuFavoriteSchedule ParseSchedule(JsonElement root) => new(
        RequireString(root, "frequency"),
        RequireString(root, "time"),
        OptionalInt32(root, "weekday", true),
        OptionalString(root, "timezone") ?? throw Protocol("schedule 缺少 timezone"),
        OptionalInt64(root, "nextRunAt", nonNegative: true),
        OptionalInt64(root, "retryAt", nonNegative: true),
        OptionalInt64(root, "lastRunAt", nonNegative: true),
        OptionalString(root, "lastStatus"),
        OptionalString(root, "lastError"));

    private static JsonDocument ParseDocument(byte[] body, string operation)
    {
        try
        {
            return JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (JsonException error)
        {
            throw new DanmuApiException(DanmuApiFailureKind.Protocol, $"核心 {operation}接口返回的 JSON 无效", error);
        }
    }

    private static JsonElement RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Protocol($"{name} 必须是对象");
        }

        return value;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw Protocol($"缺少数组字段 {name}");
        }

        return value;
    }

    private static JsonElement RequireArrayValue(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Protocol($"{name} 必须是数组");
        }

        return value;
    }

    private static string RequireString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw Protocol($"缺少字符串字段 {name}");
        }

        return value.GetString() ?? throw Protocol($"字段 {name} 不能为 null");
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Protocol($"字段 {name} 必须是字符串或 null");
        }

        return value.GetString();
    }

    private static bool RequireBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Protocol($"缺少布尔字段 {name}");
        }

        return value.GetBoolean();
    }

    private static bool? OptionalBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Protocol($"字段 {name} 必须是布尔值或 null");
        }

        return value.GetBoolean();
    }

    private static int RequireInt32(JsonElement parent, string name, bool nonNegative)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw Protocol($"缺少整数���段 {name}");
        }

        return RequireNonNegativeInt32(value, name, nonNegative);
    }

    private static int? OptionalInt32(JsonElement parent, string name, bool nonNegative)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequireNonNegativeInt32(value, name, nonNegative);
    }

    private static long RequireInt64(JsonElement parent, string name, bool nonNegative)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw Protocol($"缺少整数���段 {name}");
        }

        return RequireInt64Value(value, name, nonNegative);
    }

    private static long? OptionalInt64(JsonElement parent, string name, bool nonNegative)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequireInt64Value(value, name, nonNegative);
    }

    private static long RequireInt64Value(JsonElement value, string name, bool nonNegative)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result) || (nonNegative && result < 0))
        {
            throw Protocol($"字段 {name} 必须是{(nonNegative ? "非负" : "有效")} 64 位整数");
        }

        return result;
    }

    private static int RequireNonNegativeInt32(JsonElement value, string name, bool nonNegative = true)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || (nonNegative && result < 0))
        {
            throw Protocol($"字段 {name} 必须是{(nonNegative ? "非负" : "有效")} 32 位整数");
        }

        return result;
    }

    private static double RequireNumber(JsonElement parent, string name, bool nonNegative)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            throw Protocol($"缺少数字字段 {name}");
        }

        return RequireNumberValue(value, name, nonNegative);
    }

    private static double RequireNumberValue(JsonElement value, string name, bool nonNegative)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || double.IsNaN(result) || double.IsInfinity(result) || (nonNegative && result < 0))
        {
            throw Protocol($"字段 {name} 必须是{(nonNegative ? "非负" : "有效")}数字");
        }

        return result;
    }

    private static double? OptionalNumber(JsonElement parent, string name, bool nonNegative)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return RequireNumberValue(value, name, nonNegative);
    }

    private static int ParseIntegerPosition(string position, int index)
    {
        var parts = position.Split(',');
        if (parts.Length <= index || !int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw Protocol("弹幕 p 字段格式无效");
        }

        return result;
    }

    private static double ParseDoublePosition(string position, int index)
    {
        var parts = position.Split(',');
        if (parts.Length <= index || !double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var result) || double.IsNaN(result) || double.IsInfinity(result))
        {
            throw Protocol("弹幕 p 字段格式无效");
        }

        return result;
    }

    private static int ParseColorPosition(string position)
    {
        var parts = position.Split(',');
        var colorIndex = parts.Length >= 8
            ? 3
            : parts.Length == 4
                ? 2
                : 3 < parts.Length && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    ? 3
                    : 2;
        if (parts.Length <= colorIndex || !int.TryParse(parts[colorIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var color))
        {
            throw Protocol("弹幕颜色字段无效");
        }

        return ParseColor(color);
    }

    private static int ParseColor(double value)
    {
        if (value < 0 || value > 0xFFFFFF || value != Math.Truncate(value))
        {
            throw Protocol("弹幕颜色字段必须是 0 到 16777215 之间的整数");
        }

        return (int)value;
    }

    private static string BuildMatchPayload(string fileName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("fileName", fileName.Trim());
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildFavoritePayload(string keyword, DanmuFavoriteSchedule? schedule)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("keyword", keyword.Trim());
            if (schedule is not null)
            {
                writer.WriteStartObject("schedule");
                writer.WriteString("frequency", schedule.Frequency);
                writer.WriteString("time", schedule.Time);
                if (schedule.Weekday is int weekday)
                {
                    writer.WriteNumber("weekday", weekday);
                }
                else
                {
                    writer.WriteNull("weekday");
                }

                writer.WriteString("timezone", schedule.Timezone);
                if (schedule.NextRunAt is long nextRunAt)
                {
                    writer.WriteNumber("nextRunAt", nextRunAt);
                }
                if (schedule.RetryAt is long retryAt)
                {
                    writer.WriteNumber("retryAt", retryAt);
                }
                if (schedule.LastRunAt is long lastRunAt)
                {
                    writer.WriteNumber("lastRunAt", lastRunAt);
                }
                if (schedule.LastStatus is not null)
                {
                    writer.WriteString("lastStatus", schedule.LastStatus);
                }
                if (schedule.LastError is not null)
                {
                    writer.WriteString("lastError", schedule.LastError);
                }
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("schedule");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string EffectiveToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ? RuntimeDefaults.FallbackToken : token.Trim();

    private static string ValidateFormat(string format)
    {
        var normalized = format.Trim().ToLowerInvariant();
        return normalized is "json" or "xml"
            ? normalized
            : throw new ArgumentException("弹幕格式只支持 json 或 xml", nameof(format));
    }

    private static string? OptionalMessage(JsonElement root) =>
        OptionalString(root, "errorMessage") ?? OptionalString(root, "message");

    private static DanmuApiException Protocol(string message) =>
        new(DanmuApiFailureKind.Protocol, message);

    private static string EncodeRequired(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Uri.EscapeDataString(value.Trim());
    }

    private static string Redact(string value, string? token, string? adminToken)
    {
        var result = value.Replace('\r', ' ').Replace('\n', ' ');
        foreach (var secret in new[] { token, adminToken }.Where(secret => !string.IsNullOrWhiteSpace(secret)))
        {
            result = result.Replace(secret!, "***", StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(secret!), "***", StringComparison.Ordinal);
        }

        return result;
    }

    private static HttpClient CreateDefaultClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(4),
        UseCookies = false,
    });
}
