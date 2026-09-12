using System.Text;
using DanmuApi.App.Services;

namespace DanmuApi.Tests;

/// <summary>
/// 核心本地弹幕缓存目录快路径。判据：能只靠「头 16KB + 尾 512B」拿到完整元数据；
/// 任何形状异常都必须判为不可用（回退接口 + 留诊断），不许静默把半个资源当成有效的。
/// </summary>
public sealed class LocalDanmuCacheReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-local-cache-{Guid.NewGuid():N}");
    private readonly LocalDanmuCacheReader _reader = new();

    private string CacheDirectory => Path.Combine(_root, ".cache", "local-danmu");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响用例结论。
        }
    }

    [Fact]
    public void MissingCacheDirectoryMeansEmptyList()
    {
        var result = _reader.Read(_root);

        Assert.True(result.Available);
        Assert.Empty(result.Resources);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public void ReadsMetadataFromHeadAndUpdatedAtFromTail()
    {
        // 弹幕正文刻意造得足够大：元数据在文件头、updatedAt 在文件尾，必须靠「头+尾」才读得出。
        WriteResource("逐玉|2026|tv|5", commentsCount: 4000);

        var result = _reader.Read(_root);

        Assert.True(result.Available);
        var resource = Assert.Single(result.Resources);
        Assert.Equal("逐玉|2026|tv|5", resource.ResourceKey);
        Assert.Equal("逐玉", resource.Title);
        Assert.Equal(2026, resource.Year);
        Assert.Equal("tv", resource.Type);
        Assert.Equal(1, resource.Season);
        Assert.Equal(5, resource.Episode);
        Assert.Equal("逐玉_E05_第5集_腾讯.xml", resource.Filename);
        Assert.Equal("XML", resource.Format);
        Assert.Equal("ready", resource.Status);
        Assert.Equal(4000, resource.Count);
        Assert.Equal("2026-09-12T10:00:00.000Z", resource.UpdatedAt);
        Assert.True(new FileInfo(Path.Combine(CacheDirectory, LocalDanmuCacheReader.ExpectedFileName("逐玉|2026|tv|5"))).Length > 16 * 1024,
            "用例前提：文件要比头部预算大，否则测不出头尾拼接");
    }

    [Fact]
    public void ExpectedFileNameMatchesTheCoreNodeRuntimeHash()
    {
        // oracle 由核心同款 Node 运行时算出：
        // node -e "require('crypto').createHash('sha256').update('逐玉|2026|tv|5').digest('hex')"
        Assert.Equal(
            "7d48563346a5092d31a2e35bab3bf23e9e28f01aba03c3d4005c63e77c68f79f.json",
            LocalDanmuCacheReader.ExpectedFileName("逐玉|2026|tv|5"));
        Assert.Equal(
            "019d2a09ff8ddf6591192a56740b4656c7acf49ca7341b0f68122aa22291fd5d.json",
            LocalDanmuCacheReader.ExpectedFileName("Movie Title|2024|movie|all"));
    }

    [Fact]
    public void ResourcesAreOrderedByUpdatedAtDescending()
    {
        WriteResource("甲|2026|tv|1", updatedAt: "2026-09-10T10:00:00.000Z");
        WriteResource("乙|2026|tv|1", updatedAt: "2026-09-12T10:00:00.000Z");

        var result = _reader.Read(_root);

        Assert.True(result.Available);
        Assert.Equal(["乙|2026|tv|1", "甲|2026|tv|1"], result.Resources.Select(item => item.ResourceKey));
    }

    [Fact]
    public void MetadataBeyondHeadBudgetFallsBackWithDiagnostic()
    {
        // 标题长到把 comments 标记推到 16KB 之外：快路径不成立，必须回退。
        WriteResource("超长|2026|tv|1", title: new string('长', 20000));

        var result = _reader.Read(_root);

        Assert.False(result.Available);
        Assert.Contains("字段顺序", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentsComingFirstFallsBack()
    {
        // 模拟核心改字段顺序：comments 排在最前，头部找不到 ",\"comments\":[" 标记。
        var json = """{"comments":[{"p":"1.00,1,16777215","m":"x"}],"resourceKey":"甲|2026|tv|1","title":"甲","year":2026,"type":"tv","season":1,"episode":1,"filename":"甲.xml","size":1,"format":"XML","status":"ready","count":1,"updatedAt":"2026-09-12T10:00:00.000Z"}""";
        WriteRaw(LocalDanmuCacheReader.ExpectedFileName("甲|2026|tv|1"), json);

        var result = _reader.Read(_root);

        Assert.False(result.Available);
        Assert.Contains("已回退到核心接口", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void FileNameThatDoesNotMatchResourceKeyHashFallsBack()
    {
        WriteRaw("deadbeef.json", ResourceJson("甲|2026|tv|1"));

        var result = _reader.Read(_root);

        Assert.False(result.Available);
        Assert.Contains("sha256", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptHeadFallsBack()
    {
        WriteRaw(LocalDanmuCacheReader.ExpectedFileName("甲|2026|tv|1"), "{\"resourceKey\":不是合法 JSON,\"comments\":[],\"updatedAt\":\"2026-09-12T10:00:00.000Z\"}");

        var result = _reader.Read(_root);

        Assert.False(result.Available);
    }

    [Fact]
    public void TempFilesFromTheCoreRenameAreIgnored()
    {
        WriteResource("甲|2026|tv|1");
        WriteRaw(LocalDanmuCacheReader.ExpectedFileName("甲|2026|tv|1") + ".tmp-1757654321", "{ 半写状态 ");

        var result = _reader.Read(_root);

        Assert.True(result.Available);
        Assert.Single(result.Resources);
    }

    private void WriteResource(
        string resourceKey,
        string title = "逐玉",
        int year = 2026,
        string type = "tv",
        int season = 1,
        int? episode = 5,
        string filename = "逐玉_E05_第5集_腾讯.xml",
        string updatedAt = "2026-09-12T10:00:00.000Z",
        int commentsCount = 3) =>
        WriteRaw(LocalDanmuCacheReader.ExpectedFileName(resourceKey),
            ResourceJson(resourceKey, title, year, type, season, episode, filename, updatedAt, commentsCount));

    private static string ResourceJson(
        string resourceKey,
        string title = "逐玉",
        int year = 2026,
        string type = "tv",
        int season = 1,
        int? episode = 5,
        string filename = "逐玉_E05_第5集_腾讯.xml",
        string updatedAt = "2026-09-12T10:00:00.000Z",
        int commentsCount = 3)
    {
        // 字段顺序刻意与核心的 JSON.stringify(resource) 一致：元数据在前、comments 倒数第二、updatedAt 最后。
        var comments = string.Join(",", Enumerable.Range(0, commentsCount)
            .Select(index => $"{{\"p\":\"{index}.00,1,16777215\",\"m\":\"测试弹幕 {index}\"}}"));
        var episodeText = episode is int value ? value.ToString() : "null";
        return $$"""
            {"resourceKey":"{{resourceKey}}","videoId":"v-1","title":"{{title}}","year":{{year}},"type":"{{type}}","season":{{season}},"episode":{{episodeText}},"filename":"{{filename}}","size":2048,"format":"XML","status":"ready","count":{{commentsCount}},"matchKeys":["{{title}}"],"comments":[{{comments}}],"updatedAt":"{{updatedAt}}"}
            """;
    }

    private void WriteRaw(string fileName, string content)
    {
        Directory.CreateDirectory(CacheDirectory);
        File.WriteAllText(Path.Combine(CacheDirectory, fileName), content, new UTF8Encoding(false));
    }
}
