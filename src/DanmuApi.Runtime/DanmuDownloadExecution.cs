using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DanmuApi.Runtime;

/// <summary>
/// 重试决策：与队列执行解耦，确保永久性请求/本地文件失败不会被反复重试。
/// 语义对齐 Android DownloadRetryPolicy。
/// </summary>
public static class DanmuDownloadRetryPolicy
{
    public static bool ShouldTriggerBackoff(int? httpCode, string detail)
    {
        if (httpCode is 429 or 403 or 408 or 425)
        {
            return true;
        }

        if (httpCode is >= 500 and <= 599)
        {
            return true;
        }

        var text = detail.ToLowerInvariant();
        return ContainsAny(text,
            "timeout",
            "timed out",
            "连接超时",
            "请求超时",
            "unable to resolve host",
            "unknownhost",
            "connection reset",
            "connection refused",
            "failed to connect",
            "network is unreachable",
            "broken pipe",
            "socket",
            "eof",
            "网络错误",
            "网络不可达",
            "连接失败",
            "连接重置",
            "连接被拒绝",
            "流被重置");
    }

    public static bool ShouldRebuildChainForStaleFailure(int? httpCode, string detail)
    {
        if (httpCode is 400 or 404 or 410 or 422)
        {
            return true;
        }

        var text = detail.ToLowerInvariant();
        if (httpCode == 500 && ContainsAny(text, "invalid", "无效", "不存在", "not found", "episode"))
        {
            return true;
        }

        return ContainsAny(text,
            "missing or invalid",
            "参数错误",
            "episodeid",
            "弹幕数据为空",
            "资源不存在");
    }

    public static bool ShouldRetryFailure(int? httpCode, string detail)
    {
        if (httpCode is not null)
        {
            return httpCode is 403 or 408 or 425 or 429 || httpCode is >= 500 and <= 599;
        }

        var text = detail.ToLowerInvariant();
        return !ContainsAny(text, PermanentMarkers);
    }

    private static bool ContainsAny(string text, params string[] markers) =>
        markers.Any(text.Contains);

    private static readonly string[] PermanentMarkers =
    [
        "保存目录",
        "目录不可写",
        "目录无效",
        "无法写入",
        "创建文件失败",
        "返回内容不是",
        "返回内容为空",
        "格式校验失败",
        "解析失败",
        "缺少",
        "不是有效 json",
        "核心实际返回格式",
        "参数错误",
        "资源不存在",
        "弹幕数据为空",
        "未找到匹配剧集",
    ];
}

public sealed record DanmuDirectoryFileCandidate(
    string FilePath,
    string RelativePath,
    DanmuDownloadFormat Format,
    long LastModified,
    long Bytes);

/// <summary>
/// 弹幕下载执行：请求核心弹幕接口、按格式校验、写本地目录、维护下载记录。
/// 目录为普通文件系统路径（Windows 无 SAF）。
/// </summary>
public sealed class DanmuDownloadFileService
{
    private readonly DanmuDownloadStore _store;
    private readonly Func<IDanmuApiClient> _clientFactory;

    public DanmuDownloadFileService(DanmuDownloadStore store, Func<IDanmuApiClient> clientFactory)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public DanmuDownloadStore Store => _store;

    public async Task<DanmuDownloadResult> DownloadAsync(
        DanmuDownloadInput input,
        string host,
        int port,
        string? token,
        IProgress<DanmuDownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var settings = _store.Settings;
            var saveDirectory = settings.SaveDirectory.Trim();
            if (saveDirectory.Length == 0)
            {
                throw new InvalidOperationException("请先在下载页设置中选择保存目录");
            }

            var directoryInfo = new DirectoryInfo(saveDirectory);
            if (!directoryInfo.Exists)
            {
                // 保存目录允许自动创建；创建失败仍会显式抛出，不掩盖故障。
                directoryInfo.Create();
            }

            Report(progress, 0.08, "正在请求弹幕数据");
            var client = _clientFactory();
            var payload = await client.DownloadCommentAsync(
                host,
                port,
                token,
                input.EpisodeId,
                input.Format.Value(),
                null,
                cancellationToken).ConfigureAwait(false);

            var resolvedFormat = payload.DanmuFormat?.Trim().ToLowerInvariant() ?? string.Empty;
            if (resolvedFormat.Length > 0 && !string.Equals(resolvedFormat, input.Format.Value(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"核心实际返回格式为 {resolvedFormat}，与请求的 {input.Format.Value()} 不一致");
            }

            var inspection = DanmuPayloadInspector.Inspect(payload.Body, input.Format, payload.ContentType);
            if (!inspection.Valid)
            {
                throw new InvalidOperationException(inspection.Error);
            }

            var danmuCount = payload.DanmuCount ?? inspection.Count;

            Report(progress, 0.50, "正在准备输出目录");
            var animeDirName = DanmuFileNameTemplates.SanitizeFileComponent(input.AnimeTitle);
            if (animeDirName.Length == 0)
            {
                animeDirName = "未命名剧集";
            }

            var animeDirectory = Path.Combine(directoryInfo.FullName, animeDirName);
            Directory.CreateDirectory(animeDirectory);

            var template = input.FileNameTemplate.Trim();
            if (template.Length == 0)
            {
                template = DanmuDownloadDefaults.FileNameTemplate;
            }

            var desiredName = DanmuFileNameTemplates.Render(
                template, input.Format, input.AnimeTitle, input.EpisodeTitle, input.EpisodeNo, input.EpisodeId, input.Source);
            var resolvedName = ResolveOutputFileName(animeDirectory, desiredName, input.ConflictPolicy);
            var relativeBase = animeDirName + "/" + desiredName;
            if (resolvedName is null)
            {
                var elapsed = Elapsed(startedAt);
                var skipped = new DanmuDownloadResult(
                    DownloadRecordStatus.Skipped,
                    desiredName,
                    relativeBase,
                    string.Empty,
                    0,
                    elapsed,
                    danmuCount,
                    payload.StatusCode,
                    JoinMessages("文件已存在，按策略跳过", inspection.Warning));
                AppendRecord(input, skipped);
                return skipped;
            }

            Report(progress, 0.72, "正在写入文件");
            var targetPath = Path.Combine(animeDirectory, resolvedName);
            await File.WriteAllBytesAsync(targetPath, payload.Body, cancellationToken).ConfigureAwait(false);

            Report(progress, 1.0, "下载完成");
            var relativePath = animeDirName + "/" + resolvedName;
            var success = new DanmuDownloadResult(
                DownloadRecordStatus.Success,
                resolvedName,
                relativePath,
                targetPath,
                payload.Body.LongLength,
                Elapsed(startedAt),
                danmuCount,
                payload.StatusCode,
                inspection.Warning);
            AppendRecord(input, success);
            return success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidOperationException or DanmuApiException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            var failed = new DanmuDownloadResult(
                DownloadRecordStatus.Failed,
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                Elapsed(startedAt),
                null,
                error is DanmuApiException api ? api.StatusCode : null,
                error.Message);
            AppendRecord(input, failed);
            return failed;
        }
    }

    public async Task<DanmuFilePreview> LoadPreviewAsync(DanmuDownloadRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.StatusEnum != DownloadRecordStatus.Success)
        {
            throw new InvalidOperationException("只有下载成功的记录可以查看弹幕内容");
        }

        var path = record.FilePath.Trim();
        if (path.Length == 0)
        {
            throw new InvalidOperationException("该记录没有可读取的文件地址");
        }

        var format = record.FormatOrNull ?? throw new InvalidOperationException($"该记录的格式 {record.Format} 不受当前版本支持");
        if (!format.SupportsPreview())
        {
            throw new InvalidOperationException($"{format.Label()} 是二进制格式，不支持内容预览");
        }

        var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var preview = DanmuFilePreviewParser.Parse(
            payload, format, record.FileName, record.RelativePath, payload.LongLength, DanmuDownloadDefaults.PreviewLimit);
        if (preview.Count > 0 && record.DanmuCount != preview.Count)
        {
            _store.UpdateRecordDanmuCount(record.Id, preview.Count);
        }

        return preview;
    }

    public async Task<DownloadDirectorySyncResult> SyncExistingFilesAsync(CancellationToken cancellationToken = default)
    {
        var saveDirectory = _store.Settings.SaveDirectory.Trim();
        if (saveDirectory.Length == 0)
        {
            throw new InvalidOperationException("请先选择下载目录");
        }

        var root = new DirectoryInfo(saveDirectory);
        if (!root.Exists)
        {
            throw new InvalidOperationException("下载目录无效，请重新选择");
        }

        var visited = 0;
        var skipped = 0;
        var truncated = false;
        var candidates = new List<DanmuDirectoryFileCandidate>();
        var pending = new Stack<(DirectoryInfo Directory, string RelativePath, int Depth)>();
        pending.Push((root, string.Empty, 0));
        while (pending.Count > 0 && visited < DanmuDownloadDefaults.MaximumScanFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, relativePath, depth) = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = directory.GetFileSystemInfos();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            foreach (var child in children)
            {
                var name = child.Name.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                var childRelative = relativePath.Length == 0 ? name : relativePath + "/" + name;
                if (child is DirectoryInfo subDirectory)
                {
                    if (depth < DanmuDownloadDefaults.MaximumScanDepth)
                    {
                        pending.Push((subDirectory, childRelative, depth + 1));
                    }

                    continue;
                }

                if (child is not FileInfo file)
                {
                    continue;
                }

                visited++;
                if (visited > DanmuDownloadDefaults.MaximumScanFiles)
                {
                    truncated = true;
                    break;
                }

                var format = DanmuDownloadFormatExtensions.FromFileName(name);
                if (format is null)
                {
                    skipped++;
                    continue;
                }

                candidates.Add(new DanmuDirectoryFileCandidate(
                    file.FullName,
                    childRelative,
                    format.GetValueOrDefault(),
                    new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    file.Length));
            }
        }

        if (pending.Count > 0 || visited >= DanmuDownloadDefaults.MaximumScanFiles)
        {
            truncated = true;
        }

        var ordered = candidates
            .OrderByDescending(candidate => candidate.LastModified)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count > DanmuDownloadDefaults.MaximumRecords)
        {
            skipped += ordered.Count - DanmuDownloadDefaults.MaximumRecords;
            truncated = true;
            ordered = ordered.Take(DanmuDownloadDefaults.MaximumRecords).ToList();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var imported = new List<DanmuDownloadRecord>();
        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inspection = await InspectDirectoryFileAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (inspection is null || !inspection.Valid)
            {
                skipped++;
                continue;
            }

            var fileName = Path.GetFileName(candidate.FilePath);
            var baseName = fileName.EndsWith("." + candidate.Format.Extension(), StringComparison.OrdinalIgnoreCase)
                ? fileName[..^("." + candidate.Format.Extension()).Length]
                : fileName;
            var animeTitle = Path.GetFileName(Path.GetDirectoryName(candidate.FilePath));
            imported.Add(new DanmuDownloadRecord(
                StableDirectoryRecordId(candidate.FilePath),
                candidate.LastModified > 0 ? candidate.LastModified : now,
                string.IsNullOrWhiteSpace(animeTitle) ? "已有弹幕" : animeTitle,
                string.IsNullOrWhiteSpace(baseName) ? fileName : baseName,
                0,
                DanmuDownloadParsing.ExtractEpisodeNumber(baseName),
                "目录同步",
                candidate.Format.Value(),
                DownloadRecordStatus.Success.Key(),
                fileName,
                candidate.RelativePath,
                candidate.FilePath,
                0,
                candidate.Bytes,
                inspection.Count,
                null,
                null,
                0));
        }

        var (result, _) = _store.MergeSyncedRecords(imported);
        return result with { ScannedFiles = Math.Min(visited, DanmuDownloadDefaults.MaximumScanFiles), SkippedFiles = skipped, Truncated = truncated };
    }

    public async Task<DanmuDownloadInput> RebuildChainAsync(
        DanmuDownloadTask task,
        string host,
        int port,
        string? token,
        CancellationToken cancellationToken = default)
    {
        var keyword = DanmuDownloadParsing.ExtractAnimeKeywordForSearch(task.AnimeTitle);
        if (keyword.Length == 0)
        {
            throw new InvalidOperationException("剧名为空，无法重建链路");
        }

        var client = _clientFactory();
        var search = await client.SearchAnimeAsync(host, port, token, keyword, cancellationToken).ConfigureAwait(false);
        if (!search.Success || search.Animes.Count == 0)
        {
            throw new InvalidOperationException("未搜索到匹配动漫");
        }

        var taskAnimeKey = DanmuDownloadParsing.NormalizeAnimeTitleForMatch(task.AnimeTitle);
        var taskSourceKey = DanmuDownloadParsing.CanonicalSourceKey(
            string.IsNullOrWhiteSpace(task.Source) ? DanmuDownloadParsing.ExtractSourceFromAnimeTitle(task.AnimeTitle) : task.Source);

        var exactMatches = search.Animes.Where(candidate =>
        {
            var titleMatches = DanmuDownloadParsing.NormalizeAnimeTitleForMatch(candidate.AnimeTitle) == taskAnimeKey;
            var sourceMatches = taskSourceKey == "unknown" ||
                DanmuDownloadParsing.CanonicalSourceKey(DanmuDownloadParsing.ExtractSourceFromAnimeTitle(candidate.AnimeTitle)) == taskSourceKey;
            return titleMatches && sourceMatches;
        });
        var titleMatches = search.Animes.Where(candidate =>
            DanmuDownloadParsing.NormalizeAnimeTitleForMatch(candidate.AnimeTitle) == taskAnimeKey);
        var sourceMatches = search.Animes.Where(candidate =>
            taskSourceKey != "unknown" &&
            DanmuDownloadParsing.CanonicalSourceKey(DanmuDownloadParsing.ExtractSourceFromAnimeTitle(candidate.AnimeTitle)) == taskSourceKey);
        var ordered = exactMatches.Concat(titleMatches).Concat(sourceMatches).Concat(search.Animes)
            .DistinctBy(candidate => candidate.AnimeId)
            .ToArray();

        foreach (var candidate in ordered)
        {
            DanmuBangumiResult bangumi;
            try
            {
                bangumi = await client.GetBangumiAsync(host, port, token, candidate.AnimeId, cancellationToken).ConfigureAwait(false);
            }
            catch (DanmuApiException)
            {
                continue;
            }

            if (!bangumi.Success || bangumi.Bangumi is null)
            {
                continue;
            }

            var fallbackSource = DanmuDownloadParsing.ExtractSourceFromAnimeTitle(candidate.AnimeTitle);
            var episodes = bangumi.Bangumi.Episodes
                .Select((episode, index) => DanmuDownloadParsing.ToEpisodeCandidate(episode, fallbackSource, index + 1))
                .ToArray();
            var matched = MatchEpisodeForRebuild(task, episodes);
            if (matched is null)
            {
                continue;
            }

            return new DanmuDownloadInput(
                task.ApiBaseUrl,
                string.IsNullOrWhiteSpace(candidate.AnimeTitle) ? task.AnimeTitle : candidate.AnimeTitle,
                string.IsNullOrWhiteSpace(matched.Title) ? task.EpisodeTitle : matched.Title,
                matched.EpisodeId,
                matched.EpisodeNumber > 0 ? matched.EpisodeNumber : task.EpisodeNo,
                string.IsNullOrWhiteSpace(matched.Source) ? task.Source : matched.Source,
                DanmuDownloadFormatExtensions.FromValue(task.Format),
                task.FileNameTemplate,
                DownloadConflictPolicyExtensions.FromKey(task.ConflictPolicy),
                candidate.AnimeId);
        }

        var sourceText = string.IsNullOrWhiteSpace(task.Source) ? "unknown" : task.Source;
        throw new InvalidOperationException($"未找到匹配剧集：第{task.EpisodeNo}集（{sourceText}）");
    }

    private static DanmuEpisodeCandidate? MatchEpisodeForRebuild(DanmuDownloadTask task, IReadOnlyList<DanmuEpisodeCandidate> episodes)
    {
        if (episodes.Count == 0)
        {
            return null;
        }

        var taskSource = DanmuDownloadParsing.CanonicalSourceKey(task.Source);
        var sameSourceEpisodes = taskSource == "unknown"
            ? episodes
            : episodes.Where(episode => DanmuDownloadParsing.CanonicalSourceKey(episode.Source) == taskSource).ToArray();
        var taskTitleKey = DanmuDownloadParsing.NormalizeEpisodeTitleForMatch(task.EpisodeTitle);
        var pool = sameSourceEpisodes.Count > 0 ? sameSourceEpisodes : episodes;
        return pool.FirstOrDefault(episode => episode.EpisodeNumber == task.EpisodeNo) ??
            (taskTitleKey.Length > 0 ? pool.FirstOrDefault(episode => DanmuDownloadParsing.NormalizeEpisodeTitleForMatch(episode.Title) == taskTitleKey) : null) ??
            episodes.FirstOrDefault(episode => episode.EpisodeId == task.EpisodeId);
    }

    private static async Task<DanmuPayloadInspection?> InspectDirectoryFileAsync(DanmuDirectoryFileCandidate candidate, CancellationToken cancellationToken)
    {
        if (candidate.Bytes is > 0 and <= DanmuDownloadDefaults.MaximumScanInspectBytes)
        {
            try
            {
                var payload = await File.ReadAllBytesAsync(candidate.FilePath, cancellationToken).ConfigureAwait(false);
                return DanmuPayloadInspector.Inspect(payload, candidate.Format);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (candidate.Bytes > DanmuDownloadDefaults.MaximumScanInspectBytes)
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(candidate.FilePath);
            var prefix = new byte[64 * 1024];
            var read = await stream.ReadAsync(prefix.AsMemory(0, prefix.Length), cancellationToken).ConfigureAwait(false);
            return PlausiblePayloadPrefix(prefix.AsSpan(0, read), candidate.Format)
                ? new DanmuPayloadInspection(true)
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool PlausiblePayloadPrefix(ReadOnlySpan<byte> prefix, DanmuDownloadFormat format)
    {
        if (format == DanmuDownloadFormat.DanuniBinPb)
        {
            return true;
        }

        var text = Encoding.UTF8.GetString(prefix.ToArray()).TrimStart('\uFEFF').TrimStart();
        return format.PayloadKind() switch
        {
            DanmuPayloadKind.Xml => text.StartsWith("<?xml", StringComparison.Ordinal) || text.StartsWith("<i", StringComparison.Ordinal),
            DanmuPayloadKind.Json => format switch
            {
                DanmuDownloadFormat.Json or DanmuDownloadFormat.DdplayJson => text.StartsWith('{') && text.Contains("\"comments\"", StringComparison.Ordinal),
                DanmuDownloadFormat.ArtplayerJson or DanmuDownloadFormat.VodJson => text.StartsWith('{') && text.Contains("\"danmuku\"", StringComparison.Ordinal),
                DanmuDownloadFormat.BahaJson => text.StartsWith('{') && text.Contains("\"data\"", StringComparison.Ordinal) && text.Contains("\"danmu\"", StringComparison.Ordinal),
                DanmuDownloadFormat.DanuniJson => text.StartsWith('['),
                DanmuDownloadFormat.DplayerJson => text.StartsWith('{') && text.Contains("\"data\"", StringComparison.Ordinal),
                _ => false,
            },
            _ => true,
        };
    }

    private static string? ResolveOutputFileName(string directory, string desiredName, DownloadConflictPolicy policy)
    {
        if (!File.Exists(Path.Combine(directory, desiredName)))
        {
            return desiredName;
        }

        switch (policy)
        {
            case DownloadConflictPolicy.Skip:
                return null;
            case DownloadConflictPolicy.Overwrite:
                return desiredName;
            default:
            {
                var (baseName, extension) = SplitFileName(desiredName);
                for (var index = 1; index < 9999; index++)
                {
                    var candidate = extension.Length == 0
                        ? $"{baseName}({index})"
                        : $"{baseName}({index}).{extension}";
                    if (!File.Exists(Path.Combine(directory, candidate)))
                    {
                        return candidate;
                    }
                }

                throw new InvalidOperationException($"文件重命名次数超限：{desiredName}");
            }
        }
    }

    private static (string BaseName, string Extension) SplitFileName(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? (name, string.Empty) : (name[..dot], name[(dot + 1)..]);
    }

    private void AppendRecord(DanmuDownloadInput input, DanmuDownloadResult result)
    {
        _store.AppendRecord(new DanmuDownloadRecord(
            0,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            input.AnimeTitle,
            input.EpisodeTitle,
            input.EpisodeId,
            input.EpisodeNo,
            input.Source,
            input.Format.Value(),
            result.Status.Key(),
            result.FileName,
            result.RelativePath,
            result.FilePath,
            result.DurationMs,
            result.Bytes,
            result.DanmuCount,
            result.HttpCode,
            result.ErrorMessage,
            input.AnimeId));
    }

    private static string JoinMessages(params string?[] parts) =>
        string.Join("；", parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static void Report(IProgress<DanmuDownloadProgress>? progress, double value, string detail) =>
        progress?.Report(new DanmuDownloadProgress(value, detail));

    private static long Elapsed(long startedAt) =>
        Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - startedAt);

    private static long StableDirectoryRecordId(string path)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(path));
        var value = BitConverter.ToInt64(digest, 0) & long.MaxValue;
        return value == 0 ? 1 : value;
    }
}
