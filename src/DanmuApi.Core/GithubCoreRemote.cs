using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Core;

public sealed class GithubCoreRemote : IGithubCoreRemote
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly Uri ApiRoot = new("https://api.github.com/", UriKind.Absolute);
    private readonly HttpClient _officialHttpClient;
    private readonly HttpClient _publicHttpClient;
    private readonly IGithubTokenProvider _tokenProvider;
    private readonly IGithubRoutePreferenceStore _routePreferences;
    private readonly TimeSpan _requestTimeout;

    public GithubCoreRemote(HttpClient httpClient, IGithubTokenProvider tokenProvider)
        : this(
            httpClient,
            httpClient,
            tokenProvider,
            new ConfirmedOriginalRoutePreferenceStore())
    {
    }

    public GithubCoreRemote(
        HttpClient officialHttpClient,
        HttpClient publicHttpClient,
        IGithubTokenProvider tokenProvider,
        IGithubRoutePreferenceStore routePreferences,
        TimeSpan? requestTimeout = null)
    {
        _officialHttpClient = officialHttpClient ?? throw new ArgumentNullException(nameof(officialHttpClient));
        _publicHttpClient = publicHttpClient ?? throw new ArgumentNullException(nameof(publicHttpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _routePreferences = routePreferences ?? throw new ArgumentNullException(nameof(routePreferences));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
    }

    public GithubRateLimit? LastRateLimit { get; private set; }

    public async Task<GithubRepositoryMetadata> GetRepositoryAsync(
        GithubRepositoryReference repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        using var response = await SendAsync(
            $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = RequireObject(document.RootElement, "GitHub 仓库响应");
        return new GithubRepositoryMetadata(
            RequireString(root, "full_name"),
            RequireString(root, "default_branch"),
            OptionalString(root, "description"),
            OptionalBoolean(root, "private") ?? false);
    }

    public async Task<IReadOnlyList<GithubBranch>> GetAllBranchesAsync(
        GithubRepositoryReference repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        var result = new List<GithubBranch>();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(
                $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}/branches?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var array = RequireArray(document.RootElement, "GitHub 分支响应");
            foreach (var item in array.EnumerateArray())
            {
                var branch = RequireObject(item, "GitHub 分支");
                var commit = RequireObject(RequireProperty(branch, "commit"), "GitHub 分支 commit");
                result.Add(new GithubBranch(
                    RequireString(branch, "name"),
                    RequireString(commit, "sha"),
                    OptionalBoolean(branch, "protected") ?? false));
            }

            if (array.GetArrayLength() < 100)
            {
                return result;
            }
        }
    }

    public async Task<GithubCommit> GetCommitAsync(
        GithubRepositoryReference repository,
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        using var response = await SendAsync(
            $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}/commits/{Encode(reference)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseCommit(RequireObject(document.RootElement, "GitHub commit 响应"));
    }

    public async Task<GithubCommitPage> GetCommitsAsync(
        GithubRepositoryReference repository,
        string reference,
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ValidatePage(page, pageSize);
        using var response = await SendAsync(
            $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}/commits?sha={Encode(reference)}&per_page={pageSize.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var hasNext = HasNextLink(response.Headers);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var array = RequireArray(document.RootElement, "GitHub commit 列表");
        var items = array.EnumerateArray().Select(item => ParseCommit(RequireObject(item, "GitHub commit"))).ToArray();
        return new GithubCommitPage(
            items,
            page,
            page > 1,
            hasNext || (response.Headers.TryGetValues("Link", out _) is false && items.Length == pageSize));
    }

    public async Task<GithubCommitDetails> GetCommitDetailsAsync(
        GithubRepositoryReference repository,
        string sha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        GithubCommit? commit = null;
        var files = new List<GithubFileChange>();
        var additions = 0;
        var deletions = 0;
        var total = 0;
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(
                $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}/commits/{Encode(sha)}?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var hasNext = HasNextLink(response.Headers);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = RequireObject(document.RootElement, "GitHub commit 详情");
            commit ??= ParseCommit(root);
            if (page == 1 && root.TryGetProperty("stats", out var statsElement) && statsElement.ValueKind != JsonValueKind.Null)
            {
                var stats = RequireObject(statsElement, "GitHub commit stats");
                additions = OptionalInt(stats, "additions") ?? 0;
                deletions = OptionalInt(stats, "deletions") ?? 0;
                total = OptionalInt(stats, "total") ?? 0;
            }

            var pageFiles = RequireArrayProperty(root, "files");
            files.AddRange(pageFiles.EnumerateArray().Select(item => ParseFile(RequireObject(item, "GitHub 文件变动"))));
            if (!hasNext && pageFiles.GetArrayLength() < 100)
            {
                break;
            }
        }

        return new GithubCommitDetails(
            commit ?? throw Protocol("GitHub commit 详情缺少提交信息"),
            files,
            additions,
            deletions,
            total > 0 ? total : files.Count);
    }

    public async Task<GithubPullRequestPage> GetPullRequestsAsync(
        GithubRepositoryReference repository,
        string baseBranch,
        string state = "open",
        int page = 1,
        int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        if (state is not ("open" or "closed" or "all"))
        {
            throw new ArgumentException("PR 状态必须是 open、closed 或 all", nameof(state));
        }

        ValidatePage(page, pageSize);
        using var response = await SendAsync(
            $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}/pulls?state={state}&base={Encode(baseBranch)}&sort=updated&direction=desc&per_page={pageSize.ToString(CultureInfo.InvariantCulture)}&page={page.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var hasNext = HasNextLink(response.Headers);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var array = RequireArray(document.RootElement, "GitHub PR 列表");
        var items = array.EnumerateArray().Select(item => ParsePullRequest(RequireObject(item, "GitHub PR"))).ToArray();
        return new GithubPullRequestPage(
            items,
            page,
            page > 1,
            hasNext || (response.Headers.TryGetValues("Link", out _) is false && items.Length == pageSize));
    }

    public async Task<GithubPullRequest> GetPullRequestAsync(
        GithubRepositoryReference repository,
        int number,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        using var response = await SendAsync(
            $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParsePullRequest(RequireObject(document.RootElement, "GitHub PR 详情"));
    }

    public async Task<IReadOnlyList<GithubFileChange>> GetAllPullRequestFilesAsync(
        GithubRepositoryReference repository,
        int number,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (number <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        var files = new List<GithubFileChange>();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(
                $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}/pulls/{number.ToString(CultureInfo.InvariantCulture)}/files?per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var hasNext = HasNextLink(response.Headers);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var array = RequireArray(document.RootElement, "GitHub PR 文件列表");
            files.AddRange(array.EnumerateArray().Select(item => ParseFile(RequireObject(item, "GitHub PR 文件"))));
            if (!hasNext && array.GetArrayLength() < 100)
            {
                return files;
            }
        }
    }

    public async Task<GithubCompareResult> GetCompareAsync(
        GithubRepositoryReference repository,
        string baseSha,
        string headSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        using var response = await SendAsync(
            $"repos/{Encode(repository.Owner)}/{Encode(repository.Repository)}/compare/" +
            $"{Encode(baseSha.Trim())}...{Encode(headSha.Trim())}?per_page=250",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = RequireObject(document.RootElement, "GitHub compare 响应");
        var status = RequireString(root, "status");
        var aheadBy = RequireInt(root, "ahead_by");
        var behindBy = RequireInt(root, "behind_by");
        var totalCommits = RequireInt(root, "total_commits");
        var commits = RequireArrayProperty(root, "commits").EnumerateArray()
            .Select(item => ParseCommit(RequireObject(item, "GitHub compare commit")))
            .ToArray();
        var files = root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind != JsonValueKind.Null
            ? RequireArray(filesElement, "GitHub compare files").EnumerateArray()
                .Select(item => ParseFile(RequireObject(item, "GitHub compare 文件变动")))
                .ToArray()
            : [];
        var additions = 0;
        var deletions = 0;
        if (root.TryGetProperty("stats", out var statsElement) && statsElement.ValueKind != JsonValueKind.Null)
        {
            var stats = RequireObject(statsElement, "GitHub compare stats");
            additions = OptionalInt(stats, "additions") ?? 0;
            deletions = OptionalInt(stats, "deletions") ?? 0;
        }

        return new GithubCompareResult(
            status,
            aheadBy,
            behindBy,
            totalCommits,
            commits,
            files,
            additions,
            deletions,
            totalCommits > commits.Length,
            files.Length >= 300);
    }

    public async Task<GithubRateLimit> GetRateLimitAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendOfficialAsync("rate_limit", cancellationToken: cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = RequireObject(document.RootElement, "GitHub 额度响应");
        var resources = RequireObject(RequireProperty(root, "resources"), "GitHub resources");
        var core = RequireObject(RequireProperty(resources, "core"), "GitHub core 额度");
        var limit = RequireInt(core, "limit");
        var remaining = RequireInt(core, "remaining");
        var used = OptionalInt(core, "used") ?? checked(limit - remaining);
        var reset = RequireLong(core, "reset");
        if (limit < 0 || remaining < 0 || used < 0 || reset < 0)
        {
            throw Protocol("GitHub 额度字段不能为负数");
        }

        var result = new GithubRateLimit(
            limit,
            remaining,
            used,
            DateTimeOffset.FromUnixTimeSeconds(reset),
            _tokenProvider.IsConfigured);
        LastRateLimit = result;
        return result;
    }

    public async Task<string?> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeToken(token);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("GitHub Token 不能为空", nameof(token));
        }

        using var response = await SendOfficialAsync("user", normalized, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return RequireString(RequireObject(document.RootElement, "GitHub 用户响应"), "login");
    }

    private async Task<HttpResponseMessage> SendAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var preference = _routePreferences.Read();
        if (!preference.Confirmed)
        {
            throw new GithubRemoteException(
                GithubFailureKind.RouteSelectionRequired,
                "访问 GitHub 前需要先测速并选择下载线路");
        }

        var officialUri = BuildOfficialUri(relativePath);
        var token = NormalizeToken(_tokenProvider.GetToken());
        var routeCandidates = GithubProxyCatalog.BuildDownloadCandidates(preference.ProxyId, officialUri);
        var candidates = token.Length > 0
            ? new[] { officialUri }.Concat(routeCandidates).Distinct().ToArray()
            : routeCandidates.Concat([officialUri]).Distinct().ToArray();
        return await SendCandidatesAsync(candidates, token, cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendOfficialAsync(
        string relativePath,
        string? tokenOverride = null,
        CancellationToken cancellationToken = default)
    {
        var token = NormalizeToken(tokenOverride is null ? _tokenProvider.GetToken() : tokenOverride);
        return SendCandidatesAsync([BuildOfficialUri(relativePath)], token, cancellationToken);
    }

    private static Uri BuildOfficialUri(string relativePath)
    {
        var requestUri = new Uri(ApiRoot, relativePath);
        if (!string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(requestUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GitHub API 请求主机边界校验失败");
        }

        return requestUri;
    }

    private async Task<HttpResponseMessage> SendCandidatesAsync(
        IReadOnlyList<Uri> candidates,
        string normalizedToken,
        CancellationToken cancellationToken)
    {
        Exception? lastNetworkError = null;
        GithubRemoteException? lastHttpError = null;
        foreach (var candidate in candidates)
        {
            var isOfficialApi = string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(candidate.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.ParseAdd("DanmuApiWindows/0.1");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (isOfficialApi && normalizedToken.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalizedToken);
            }

            HttpResponseMessage? response = null;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_requestTimeout);
            try
            {
                response = await (isOfficialApi ? _officialHttpClient : _publicHttpClient).SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token).ConfigureAwait(false);
                var body = await ReadBoundedTextAsync(response,
                    response.IsSuccessStatusCode ? MaxResponseBytes : 64 * 1024, deadline.Token).ConfigureAwait(false);
                response.Content.Dispose();
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
            {
                response?.Dispose();
                lastNetworkError = deadline.IsCancellationRequested
                    ? new TimeoutException($"GitHub 请求 {candidate.Host} 超过 {_requestTimeout.TotalSeconds:0.##} 秒（含响应体读取）", error)
                    : error;
                continue;
            }
            catch
            {
                response?.Dispose();
                throw;
            }

            UpdateRateLimitFromHeaders(response.Headers, isOfficialApi && normalizedToken.Length > 0);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                var body = await ReadBoundedTextAsync(response, 64 * 1024, cancellationToken).ConfigureAwait(false);
                var apiMessage = TryReadErrorMessage(body);
                var status = (int)response.StatusCode;
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => GithubFailureKind.Authentication,
                    HttpStatusCode.NotFound => GithubFailureKind.NotFound,
                    HttpStatusCode.Forbidden when LastRateLimit?.Remaining == 0 => GithubFailureKind.RateLimited,
                    HttpStatusCode.TooManyRequests => GithubFailureKind.RateLimited,
                    HttpStatusCode.Forbidden => GithubFailureKind.Forbidden,
                    _ => GithubFailureKind.Http,
                };
                lastHttpError = new GithubRemoteException(
                    kind,
                    string.IsNullOrWhiteSpace(apiMessage)
                        ? $"GitHub API 请求失败（HTTP {status.ToString(CultureInfo.InvariantCulture)}）"
                        : $"GitHub API 请求失败（HTTP {status.ToString(CultureInfo.InvariantCulture)}）：{Sanitize(apiMessage, normalizedToken)}",
                    status);
                if (isOfficialApi && normalizedToken.Length > 0 &&
                    kind is GithubFailureKind.Authentication or GithubFailureKind.Forbidden or GithubFailureKind.RateLimited)
                {
                    throw lastHttpError;
                }
            }
        }

        if (lastHttpError is not null)
        {
            throw lastHttpError;
        }

        throw new GithubRemoteException(
            GithubFailureKind.Network,
            $"GitHub 网络请求失败：{Sanitize(lastNetworkError?.Message ?? "所有线路均不可达", normalizedToken)}",
            innerException: lastNetworkError);
    }

    private void UpdateRateLimitFromHeaders(HttpResponseHeaders headers, bool authenticated)
    {
        if (!TryHeaderInt(headers, "X-RateLimit-Limit", out var limit) ||
            !TryHeaderInt(headers, "X-RateLimit-Remaining", out var remaining) ||
            !TryHeaderLong(headers, "X-RateLimit-Reset", out var reset))
        {
            return;
        }

        var used = TryHeaderInt(headers, "X-RateLimit-Used", out var parsedUsed)
            ? parsedUsed
            : checked(limit - remaining);
        if (limit >= 0 && remaining >= 0 && used >= 0 && reset >= 0)
        {
            LastRateLimit = new GithubRateLimit(
                limit,
                remaining,
                used,
                DateTimeOffset.FromUnixTimeSeconds(reset),
                authenticated);
        }
    }

    private sealed class ConfirmedOriginalRoutePreferenceStore : IGithubRoutePreferenceStore
    {
        public GithubRoutePreference Read() => new(GithubProxyCatalog.OriginalId, true);
        public void Confirm(string proxyId) => throw new NotSupportedException();
        public void Invalidate() => throw new NotSupportedException();
    }

    private static GithubCommit ParseCommit(JsonElement root)
    {
        var sha = RequireString(root, "sha");
        var commit = RequireObject(RequireProperty(root, "commit"), "GitHub commit 内容");
        var message = RequireString(commit, "message");
        string? author = null;
        DateTimeOffset? committedAt = null;
        if (commit.TryGetProperty("author", out var authorElement) && authorElement.ValueKind != JsonValueKind.Null)
        {
            var authorObject = RequireObject(authorElement, "GitHub commit author");
            author = OptionalString(authorObject, "name");
            committedAt = OptionalDate(authorObject, "date");
        }

        var parents = root.TryGetProperty("parents", out var parentsElement) && parentsElement.ValueKind != JsonValueKind.Null
            ? RequireArray(parentsElement, "GitHub commit parents").EnumerateArray()
                .Select(parent => RequireString(RequireObject(parent, "GitHub parent commit"), "sha"))
                .ToArray()
            : [];
        return new GithubCommit(
            sha,
            message.Split('\n', 2)[0],
            message,
            author,
            committedAt,
            parents);
    }

    private static GithubFileChange ParseFile(JsonElement root)
    {
        var status = OptionalString(root, "status") ?? "modified";
        var patch = OptionalString(root, "patch");
        return new GithubFileChange(
            RequireString(root, "filename"),
            OptionalString(root, "previous_filename"),
            status,
            OptionalInt(root, "additions") ?? 0,
            OptionalInt(root, "deletions") ?? 0,
            OptionalInt(root, "changes") ?? 0,
            patch,
            patch is null ? "GitHub 未提供 patch（文件可能是二进制或变更过大）" : null);
    }

    private static GithubPullRequest ParsePullRequest(JsonElement root)
    {
        var head = RequireObject(RequireProperty(root, "head"), "GitHub PR head");
        var headRepository = RequireObject(RequireProperty(head, "repo"), "GitHub PR head repo");
        var @base = RequireObject(RequireProperty(root, "base"), "GitHub PR base");
        string? author = null;
        if (root.TryGetProperty("user", out var userElement) && userElement.ValueKind != JsonValueKind.Null)
        {
            author = OptionalString(RequireObject(userElement, "GitHub PR user"), "login");
        }

        return new GithubPullRequest(
            RequireInt(root, "number"),
            RequireString(root, "title"),
            OptionalString(root, "body") ?? string.Empty,
            RequireString(root, "state"),
            author,
            RequireString(@base, "ref"),
            RequireString(headRepository, "full_name"),
            RequireString(head, "ref"),
            RequireString(head, "sha"),
            OptionalBoolean(root, "draft") ?? false,
            OptionalBoolean(root, "merged") ?? !string.IsNullOrWhiteSpace(OptionalString(root, "merged_at")),
            OptionalDate(root, "updated_at"),
            OptionalString(root, "html_url"),
            OptionalInt(root, "additions"),
            OptionalInt(root, "deletions"),
            OptionalInt(root, "changed_files"));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedTextAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw Protocol("GitHub 返回空响应");
        }

        try
        {
            return JsonDocument.Parse(body, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (JsonException error)
        {
            throw new GithubRemoteException(GithubFailureKind.Protocol, $"GitHub JSON 无效：{error.Message}", innerException: error);
        }
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maxBytes)
        {
            throw Protocol($"GitHub 响应超过 {maxBytes.ToString(CultureInfo.InvariantCulture)} 字节上限");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw Protocol($"GitHub 响应超过 {maxBytes.ToString(CultureInfo.InvariantCulture)} 字节上限");
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(buffer.ToArray());
        }
        catch (DecoderFallbackException error)
        {
            throw new GithubRemoteException(GithubFailureKind.Protocol, "GitHub 响应不是有效 UTF-8", innerException: error);
        }
    }

    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? OptionalString(document.RootElement, "message")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw Protocol($"GitHub 字段缺失：{name}");

    private static JsonElement RequireObject(JsonElement element, string label) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw Protocol($"{label} 必须是对象");

    private static JsonElement RequireArray(JsonElement element, string label) =>
        element.ValueKind == JsonValueKind.Array
            ? element
            : throw Protocol($"{label} 必须是数组");

    private static JsonElement RequireArrayProperty(JsonElement element, string name) =>
        RequireArray(RequireProperty(element, name), $"GitHub 字段 {name}");

    private static string RequireString(JsonElement element, string name) =>
        OptionalString(element, name) is { Length: > 0 } value
            ? value
            : throw Protocol($"GitHub 字段缺失或为空：{name}");

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw Protocol($"GitHub 字段类型错误：{name} 应为字符串或 null");
    }

    private static bool? OptionalBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw Protocol($"GitHub 字段类型错误：{name} 应为布尔值或 null");
    }

    private static int RequireInt(JsonElement element, string name) =>
        OptionalInt(element, name) ?? throw Protocol($"GitHub 字段缺失或为空：{name}");

    private static int? OptionalInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw Protocol($"GitHub 字段类型错误：{name} 应为整数或 null");
    }

    private static long RequireLong(JsonElement element, string name)
    {
        var value = RequireProperty(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : throw Protocol($"GitHub 字段类型错误：{name} 应为整数");
    }

    private static DateTimeOffset? OptionalDate(JsonElement element, string name)
    {
        var raw = OptionalString(element, name);
        if (raw is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : throw Protocol($"GitHub 日期字段无效：{name}");
    }

    private static bool HasNextLink(HttpResponseHeaders headers) =>
        headers.TryGetValues("Link", out var values) &&
        values.Any(value => value.Contains("rel=\"next\"", StringComparison.Ordinal));

    private static bool TryHeaderInt(HttpResponseHeaders headers, string name, out int result)
    {
        result = default;
        return headers.TryGetValues(name, out var values) &&
            int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryHeaderLong(HttpResponseHeaders headers, string name, out long result)
    {
        result = default;
        return headers.TryGetValues(name, out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
    }

    private static string Encode(string value) => Uri.EscapeDataString(value.Trim());

    private static string NormalizeToken(string? token)
    {
        var value = token?.Trim() ?? string.Empty;
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return value[7..].Trim();
        }

        return value.StartsWith("token ", StringComparison.OrdinalIgnoreCase)
            ? value[6..].Trim()
            : value;
    }

    private static string Sanitize(string value, string token) =>
        token.Length == 0 ? value : value.Replace(token, "***", StringComparison.Ordinal);

    private static GithubRemoteException Protocol(string message) =>
        new(GithubFailureKind.Protocol, message);
}
