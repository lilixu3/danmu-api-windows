using System.Text;
using System.Text.Json;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class DanmuDownloadModelsTests
{
    [Theory]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("artplayer.json")]
    [InlineData("baha.json")]
    [InlineData("bili.xml")]
    [InlineData("danuni.json")]
    [InlineData("danuni.binpb")]
    [InlineData("ddplay.json")]
    [InlineData("dplayer.json")]
    [InlineData("vod.json")]
    public void FormatValueRoundTrips(string value)
    {
        var format = DanmuDownloadFormatExtensions.FromValueOrNull(value);
        Assert.NotNull(format);
        Assert.Equal(value, format!.Value.Value());
    }

    [Fact]
    public void FromFileNamePrefersLongestExtension()
    {
        Assert.Equal(DanmuDownloadFormat.DanuniBinPb, DanmuDownloadFormatExtensions.FromFileName("episode.danuni.binpb"));
        Assert.Equal(DanmuDownloadFormat.Json, DanmuDownloadFormatExtensions.FromFileName("episode.json"));
        Assert.Equal(DanmuDownloadFormat.BiliXml, DanmuDownloadFormatExtensions.FromFileName("episode.bili.xml"));
        Assert.Null(DanmuDownloadFormatExtensions.FromFileName("episode.txt"));
    }

    [Fact]
    public void TemplateRenderMapsPlaceholdersAndEnsuresExtension()
    {
        var rendered = DanmuFileNameTemplates.Render(
            "{animeTitle}_E{episodeNo2}_{episodeTitle}_{source}.{ext}",
            DanmuDownloadFormat.Xml,
            "凡人修仙传",
            "再入星海",
            3,
            2334455,
            "bilibili1",
            new DateTime(2026, 9, 5, 12, 0, 0));
        Assert.Equal("凡人修仙传_E03_再入星海_bilibili1.xml", rendered);

        var noExtension = DanmuFileNameTemplates.Render("{animeTitle}-{episodeNo}", DanmuDownloadFormat.Json, "测试", "第一集", 1, 2, "qq");
        Assert.EndsWith(".json", noExtension, StringComparison.Ordinal);

        var sanitized = DanmuFileNameTemplates.Render("a/b\\c:d*?.xml", DanmuDownloadFormat.Xml, "测试", "第一集", 1, 2, "qq");
        Assert.DoesNotContain("/", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(":", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeCustomClampsValues()
    {
        var config = DownloadThrottleConfig.SanitizeCustom(1, 99_999, 0, 999_999_999, 1, 99_999_999);
        Assert.Equal(100, config.BaseDelayMs);
        Assert.Equal(20_000, config.JitterMaxMs);
        Assert.Equal(1, config.BatchSize);
        Assert.Equal(900_000, config.BatchRestMs);
        Assert.Equal(1_000, config.BackoffBaseMs);
        Assert.Equal(1_800_000, config.BackoffMaxMs);
    }

    [Theory]
    [InlineData(429, "任意", true)]
    [InlineData(404, "任意", false)]
    [InlineData(null, "保存目录无效", false)]
    [InlineData(null, "connection reset", true)]
    public void RetryPolicyClassifiesBackoff(int? httpCode, string detail, bool expected)
    {
        Assert.Equal(expected, DanmuDownloadRetryPolicy.ShouldTriggerBackoff(httpCode, detail));
    }

    [Theory]
    [InlineData(404, "not found", false)]
    [InlineData(500, "服务器错误", true)]
    [InlineData(null, "保存目录不可写", false)]
    [InlineData(null, "unknown network error", true)]
    public void RetryPolicyClassifiesRetry(int? httpCode, string detail, bool expected)
    {
        Assert.Equal(expected, DanmuDownloadRetryPolicy.ShouldRetryFailure(httpCode, detail));
    }

    [Theory]
    [InlineData(400, "", true)]
    [InlineData(404, "", true)]
    [InlineData(null, "episodeid 无效", true)]
    [InlineData(null, "普通失败", false)]
    public void RetryPolicyDetectsStaleChain(int? httpCode, string detail, bool expected)
    {
        Assert.Equal(expected, DanmuDownloadRetryPolicy.ShouldRebuildChainForStaleFailure(httpCode, detail));
    }

    [Fact]
    public void SourceParsingNormalizesCanonicalKeys()
    {
        Assert.Equal("bilibili", DanmuDownloadParsing.CanonicalSourceKey("bilibili1"));
        Assert.Equal("tencent", DanmuDownloadParsing.CanonicalSourceKey("qq"));
        Assert.Equal("unknown", DanmuDownloadParsing.CanonicalSourceKey(" "));
        Assert.Equal("bilibili1", DanmuDownloadParsing.ParseSource("【bilibili1】第1集 上"));
        Assert.Equal("第1集 上", DanmuDownloadParsing.StripSourceTag("【bilibili1】第1集 上"));
        Assert.Equal("bilibili1", DanmuDownloadParsing.ExtractSourceFromAnimeTitle("凡人修仙传 from bilibili1"));
        Assert.Equal("凡人修仙传", DanmuDownloadParsing.ExtractAnimeKeywordForSearch("凡人修仙传 from bilibili1"));
    }

    [Fact]
    public void EpisodeParsingDeduplicatesByNumberAndTitle()
    {
        var episodes = new[]
        {
            new DanmuEpisodeCandidate(11, 1, "【qq】开端", "qq", ""),
            new DanmuEpisodeCandidate(12, 1, "开端", "bilibili", ""),
            new DanmuEpisodeCandidate(13, 2, "继续", "bilibili", ""),
        };

        var deduped = DanmuDownloadParsing.DeduplicateEpisodes(episodes);

        Assert.Equal(2, deduped.Count);
        Assert.Equal(11, deduped[0].EpisodeId);
        Assert.Equal("【qq】开端", deduped[0].Title);
    }

    [Fact]
    public void ToEpisodeCandidateParsesCoreEpisodeFields()
    {
        var candidate = DanmuDownloadParsing.ToEpisodeCandidate(
            new DanmuEpisode("s1", 42, "【bilibili1】第3集 标题", "3", "", "https://example.invalid/3"),
            "qq",
            9);

        Assert.Equal(42, candidate.EpisodeId);
        Assert.Equal(3, candidate.EpisodeNumber);
        Assert.Equal("bilibili1", candidate.Source);
        Assert.Equal("第3集 标题", candidate.Title);
        Assert.Equal("https://example.invalid/3", candidate.SourceUrl);
    }
}

public sealed class DanmuDownloadStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-download-{Guid.NewGuid():N}");

    private DanmuDownloadStore CreateStore() => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DanmuDownloadInput NewInput(long episodeId = 1, string source = "qq", DanmuDownloadFormat format = DanmuDownloadFormat.Xml) => new(
        string.Empty, "测试番剧", "第一集", episodeId, 1, source, format, DanmuDownloadDefaults.FileNameTemplate, DownloadConflictPolicy.Rename, 7);

    [Fact]
    public void EnqueueDeduplicatesActiveTasksAndPersists()
    {
        var store = CreateStore();
        Assert.Equal(1, store.EnqueueTasks([NewInput()]));
        Assert.Equal(0, store.EnqueueTasks([NewInput()]));
        Assert.Equal(1, store.EnqueueTasks([NewInput(format: DanmuDownloadFormat.Json)]));

        var reopened = CreateStore();
        Assert.Equal(2, reopened.QueueTasks.Count);
        Assert.Equal("qq", reopened.QueueTasks[0].Source);
    }

    [Fact]
    public void CompletedTasksCanBeReenqueuedAndCleared()
    {
        var store = CreateStore();
        store.EnqueueTasks([NewInput()]);
        var task = store.QueueTasks[0];
        store.SetTaskStatus(task.TaskId, DownloadQueueStatus.Success, "已保存");

        Assert.Equal(1, store.EnqueueTasks([NewInput()]));

        var completed = store.QueueTasks.Single(item => item.StatusEnum == DownloadQueueStatus.Success);
        Assert.Equal(DownloadQueueStatus.Success, completed.StatusEnum);
        Assert.Equal(1, store.ClearCompletedQueueTasks());
        Assert.Single(store.QueueTasks);
        Assert.Equal(DownloadQueueStatus.Pending, store.QueueTasks[0].StatusEnum);
    }

    [Fact]
    public void MarkRunningTasksAsPendingRecovers()
    {
        var store = CreateStore();
        store.EnqueueTasks([NewInput()]);
        store.SetTaskStatus(store.QueueTasks[0].TaskId, DownloadQueueStatus.Running, "下载中");

        var reopened = CreateStore();
        Assert.Equal(1, reopened.MarkRunningTasksAsPending("应用启动时恢复"));
        Assert.Equal(DownloadQueueStatus.Pending, reopened.QueueTasks[0].StatusEnum);
        Assert.Equal("应用启动时恢复", reopened.QueueTasks[0].LastDetail);
    }

    [Fact]
    public void RecordsCapAtMaximumAndReplace()
    {
        var store = CreateStore();
        for (var index = 0; index < DanmuDownloadDefaults.MaximumRecords + 10; index++)
        {
            store.AppendRecord(new DanmuDownloadRecord(
                0, DateTimeOffset.UtcNow.AddSeconds(index).ToUnixTimeMilliseconds(), "番剧", $"第{index}集",
                index, index, "qq", "xml", DownloadRecordStatus.Success.Key(), "f", "rel", "path", 1, index, index, 200, null, 1));
        }

        var records = store.Records;
        Assert.Equal(DanmuDownloadDefaults.MaximumRecords, records.Count);
        Assert.DoesNotContain(records, record => record.EpisodeNo < 10);

        var reopened = CreateStore();
        Assert.Equal(DanmuDownloadDefaults.MaximumRecords, reopened.Records.Count);
    }

    [Fact]
    public void DeleteRecordsRetainsSharedFiles()
    {
        var store = CreateStore();
        var sharedPath = Path.Combine(_root, "shared.xml");
        store.AppendRecord(new DanmuDownloadRecord(1, 1, "番剧", "第1集", 1, 1, "qq", "xml", "success", "shared.xml", "rel", sharedPath));
        store.AppendRecord(new DanmuDownloadRecord(2, 2, "番剧", "第1集", 1, 1, "bilibili", "xml", "success", "shared.xml", "rel", sharedPath));
        Directory.CreateDirectory(_root);
        File.WriteAllText(sharedPath, "x");

        var result = store.DeleteRecords(new HashSet<long> { 1 }, deleteLocalFiles: true);

        Assert.Equal(1, result.RemovedRecords);
        Assert.Equal(1, result.RetainedSharedFiles);
        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(sharedPath));

        var result2 = store.DeleteRecords(new HashSet<long> { 2 }, deleteLocalFiles: true);
        Assert.Equal(1, result2.DeletedFiles);
        Assert.False(File.Exists(sharedPath));
    }

    [Fact]
    public void PersistFailedEventRaisedWhenPathIsBlocked()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "download"), "not a directory");
        var store = new DanmuDownloadStore(_root);
        var diagnostics = new List<string>();
        store.PersistFailed += message => diagnostics.Add(message);

        store.AppendRecord(new DanmuDownloadRecord(0, 1, "番剧", "第1集", 1, 1, "qq", "xml", "success"));

        Assert.NotEmpty(diagnostics);
        Assert.Contains("写入失败", diagnostics[0], StringComparison.Ordinal);
    }
}

public sealed class DanmuDownloadServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"danmu-dlsvc-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string SampleXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<i><d p=\"1.5,1,16777215,[qq]\">hello</d><d p=\"2.5,5,255,[bilibili]\">top</d></i>";

    [Fact]
    public void InspectorValidatesEachFormatFamily()
    {
        var json = Encoding.UTF8.GetBytes("{\"count\":2,\"comments\":[{\"m\":\"a\"},{\"m\":\"b\"}]}");
        Assert.True(DanmuPayloadInspector.Inspect(json, DanmuDownloadFormat.Json).Valid);

        var fallback = Encoding.UTF8.GetBytes("{\"success\":true,\"animes\":[]}");
        Assert.False(DanmuPayloadInspector.Inspect(fallback, DanmuDownloadFormat.Json).Valid);

        var artplayer = Encoding.UTF8.GetBytes("{\"danmuku\":[[1,1],[2,2]]}");
        var artplayerResult = DanmuPayloadInspector.Inspect(artplayer, DanmuDownloadFormat.ArtplayerJson);
        Assert.True(artplayerResult.Valid);
        Assert.Equal(2, artplayerResult.Count);

        var baha = Encoding.UTF8.GetBytes("{\"data\":{\"totalCount\":1,\"danmu\":[{\"text\":\"a\"}]}}");
        Assert.True(DanmuPayloadInspector.Inspect(baha, DanmuDownloadFormat.BahaJson).Valid);

        var xml = Encoding.UTF8.GetBytes(SampleXml);
        var xmlResult = DanmuPayloadInspector.Inspect(xml, DanmuDownloadFormat.BiliXml);
        Assert.True(xmlResult.Valid);
        Assert.Equal(2, xmlResult.Count);

        var notXml = Encoding.UTF8.GetBytes("{\"count\":0}");
        Assert.False(DanmuPayloadInspector.Inspect(notXml, DanmuDownloadFormat.Xml).Valid);

        var binaryAsText = Encoding.UTF8.GetBytes("{\"count\":0}");
        Assert.False(DanmuPayloadInspector.Inspect(binaryAsText, DanmuDownloadFormat.DanuniBinPb).Valid);

        var binary = new byte[] { 0x0A, 0x01, 0x01, 0x12, 0x02 };
        Assert.True(DanmuPayloadInspector.Inspect(binary, DanmuDownloadFormat.DanuniBinPb, "application/octet-stream").Valid);
    }

    /// <summary>
    /// 弹幕正文里的裸 <c>&amp;</c> / 非法控制字符 / 裸 <c>&lt;</c> 是高频真实情况
    /// （正文写「A &amp; B」很常见）。这三种以前会让整篇 XML 解析失败，
    /// 结果是集数虽然下载成功，却挂着一条吓人的「XML 格式检查警告」，
    /// 而且拿不到弹幕条数。现在用内存归一化容错解析，条数正确且不再报警告。
    /// </summary>
    [Theory]
    [InlineData("裸 & 符号", "<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\">坏 & 内容</d></i>")]
    [InlineData("非法控制字符", "<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\">a\u000bb</d></i>")]
    [InlineData("裸 < 符号", "<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\">a < b</d></i>")]
    [InlineData("多集混合缺陷", "<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\">a & b</d><d p=\"2,1,25,2\">c < d</d></i>")]
    public void XmlToleranceAcceptsRealWorldTextDefects(string name, string payload)
    {
        var result = DanmuPayloadInspector.Inspect(
            Encoding.UTF8.GetBytes(payload),
            DanmuDownloadFormat.Xml,
            "application/xml");

        Assert.True(result.Valid, $"{name} 必须可下载");
        Assert.Equal(string.Empty, result.Error);
        Assert.Null(result.Warning);
        Assert.NotNull(result.Count);
        Assert.True(result.Count > 0, $"{name} 必须能数出弹幕条数");
    }

    /// <summary>
    /// 容错不能把「本来就是合法 XML」的写法改坏：
    /// 已转义实体、数字实体、CDATA 都必须在容错路径前后给出同样的条数与文字。
    /// </summary>
    [Theory]
    [InlineData("&amp;")]
    [InlineData("&#38;")]
    [InlineData("&#x26;")]
    public void XmlToleranceKeepsValidEntitiesIntact(string entity)
    {
        var payload = $"<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\">A {entity} B</d></i>";
        var result = DanmuPayloadInspector.Inspect(Encoding.UTF8.GetBytes(payload), DanmuDownloadFormat.Xml);
        var preview = DanmuFilePreviewParser.Parse(
            Encoding.UTF8.GetBytes(payload), DanmuDownloadFormat.Xml, "a.xml", "a.xml", payload.Length, 5);

        Assert.True(result.Valid);
        Assert.Null(result.Warning);
        Assert.Equal(1, result.Count);
        Assert.Null(preview.ParseError);
        Assert.Equal("A & B", Assert.Single(preview.Items).Text);
    }

    [Fact]
    public void XmlToleranceKeepsCdataAndBomWorking()
    {
        var cdata = "<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\"><![CDATA[a & b < c]]></d></i>";
        var preview = DanmuFilePreviewParser.Parse(
            Encoding.UTF8.GetBytes(cdata), DanmuDownloadFormat.Xml, "a.xml", "a.xml", cdata.Length, 5);
        Assert.Null(preview.ParseError);
        Assert.Equal("a & b < c", Assert.Single(preview.Items).Text);

        var withBom = "\uFEFF<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\">x & y</d></i>";
        Assert.True(DanmuPayloadInspector.Inspect(Encoding.UTF8.GetBytes(withBom), DanmuDownloadFormat.Xml).Valid);
    }

    /// <summary>
    /// 容错不是兜底掩盖：它修不了的缺陷（标签不闭合）仍必须显式给出警告，
    /// 非 XML（HTML 错误页、核心 JSON 回退）仍必须是硬失败。
    /// </summary>
    [Fact]
    public void XmlToleranceStillReportsWhatItCannotFix()
    {
        var unclosed = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\">a</i>");
        var unclosedResult = DanmuPayloadInspector.Inspect(unclosed, DanmuDownloadFormat.Xml);
        Assert.True(unclosedResult.Valid);
        Assert.NotNull(unclosedResult.Warning);
        Assert.Contains("XML 解析失败", unclosedResult.Warning, StringComparison.Ordinal);

        var html = DanmuPayloadInspector.Inspect(Encoding.UTF8.GetBytes("<html>403 Forbidden</html>"), DanmuDownloadFormat.Xml, "text/html");
        Assert.False(html.Valid);
        Assert.Contains("不是 XML", html.Error, StringComparison.Ordinal);

        var jsonFallback = DanmuPayloadInspector.Inspect(Encoding.UTF8.GetBytes("{\"count\":0}"), DanmuDownloadFormat.Xml);
        Assert.False(jsonFallback.Valid);
        Assert.Contains("不是 XML", jsonFallback.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// 容错归一化只作用于内存副本：写盘的文件必须与核心返回的字节逐一相同。
    /// 这条是红线——绝不能为了让解析通过而篡改用户下载到的文件。
    /// </summary>
    [Fact]
    public async Task XmlToleranceNeverRewritesDownloadedBytes()
    {
        var payload = "<?xml version=\"1.0\"?><i><d p=\"1,1,25,1\">坏 & 内容</d></i>";
        var store = new DanmuDownloadStore(_root);
        store.SaveSettings(new DanmuDownloadSettings(SaveDirectory: Path.Combine(_root, "save")));
        var service = new DanmuDownloadFileService(store, () => new FakeDownloadClient(payload, 1));
        var input = new DanmuDownloadInput(
            string.Empty, "测试番剧", "第一集", 42, 1, "qq", DanmuDownloadFormat.Xml,
            DanmuDownloadDefaults.FileNameTemplate, DownloadConflictPolicy.Rename, 7);

        var result = await service.DownloadAsync(input, "127.0.0.1", 9321, "token", null);

        Assert.Equal(DownloadRecordStatus.Success, result.Status);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(1, result.DanmuCount);
        Assert.True(File.Exists(result.FilePath));
        Assert.Equal(
            Encoding.UTF8.GetBytes(payload),
            await File.ReadAllBytesAsync(result.FilePath));
    }

    [Fact]
    public void PreviewParserMapsXmlAndJsonItems()
    {
        var xmlPreview = DanmuFilePreviewParser.Parse(
            Encoding.UTF8.GetBytes(SampleXml), DanmuDownloadFormat.Xml, "a.xml", "a/a.xml", 100);
        Assert.Equal(2, xmlPreview.Count);
        Assert.Equal("hello", xmlPreview.Items[0].Text);
        Assert.Equal("1.5", xmlPreview.Items[0].TimeSeconds!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("qq", xmlPreview.Items[0].Source);
        Assert.Equal("5", xmlPreview.Items[1].Mode);

        var jsonPreview = DanmuFilePreviewParser.Parse(
            Encoding.UTF8.GetBytes("{\"count\":1,\"comments\":[{\"p\":\"3,4,16711680,[bilibili]\",\"m\":\"底部\"}]}"),
            DanmuDownloadFormat.Json, "a.json", "a/a.json", 100);
        Assert.Equal(1, jsonPreview.Count);
        Assert.Equal("4", jsonPreview.Items[0].Mode);
        Assert.Equal("底部", jsonPreview.Items[0].Text);

        var dplayerPreview = DanmuFilePreviewParser.Parse(
            Encoding.UTF8.GetBytes("{\"code\":0,\"data\":[[1.5,1,\"#FFFFFF\",\"qq\",\"你好\"]]}"),
            DanmuDownloadFormat.DplayerJson, "a.json", "a/a.json", 100);
        Assert.Equal("你好", dplayerPreview.Items[0].Text);
        Assert.Equal("qq", dplayerPreview.Items[0].Source);
    }

    [Fact]
    public async Task DownloadWritesFileAppendsRecordAndHonorsConflictPolicy()
    {
        var store = new DanmuDownloadStore(_root);
        store.SaveSettings(new DanmuDownloadSettings(SaveDirectory: Path.Combine(_root, "save")));
        var service = new DanmuDownloadFileService(store, () => new FakeDownloadClient(SampleXml, 12));
        var input = new DanmuDownloadInput(
            string.Empty, "测试番剧", "第一集", 42, 1, "qq", DanmuDownloadFormat.Xml,
            DanmuDownloadDefaults.FileNameTemplate, DownloadConflictPolicy.Rename, 7);

        var result = await service.DownloadAsync(input, "127.0.0.1", 9321, "token", null);

        Assert.Equal(DownloadRecordStatus.Success, result.Status);
        Assert.Equal(12, result.DanmuCount);
        Assert.True(File.Exists(result.FilePath));
        var record = Assert.Single(store.Records);
        Assert.Equal(DownloadRecordStatus.Success, record.StatusEnum);
        Assert.Equal("测试番剧", record.AnimeTitle);
        Assert.Contains("测试番剧", record.RelativePath, StringComparison.Ordinal);

        var skipped = await service.DownloadAsync(input with { ConflictPolicy = DownloadConflictPolicy.Skip }, "127.0.0.1", 9321, "token", null);
        Assert.Equal(DownloadRecordStatus.Skipped, skipped.Status);

        var renamed = await service.DownloadAsync(input, "127.0.0.1", 9321, "token", null);
        Assert.Equal(DownloadRecordStatus.Success, renamed.Status);
        Assert.NotEqual(result.FileName, renamed.FileName);
        Assert.Contains("(1)", renamed.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadRejectsFormatMismatchAndCreatesFailedRecord()
    {
        var store = new DanmuDownloadStore(_root);
        store.SaveSettings(new DanmuDownloadSettings(SaveDirectory: Path.Combine(_root, "save")));
        var service = new DanmuDownloadFileService(store, () => new FakeDownloadClient(SampleXml, null, declaredFormat: "json"));
        var input = new DanmuDownloadInput(
            string.Empty, "测试番剧", "第一集", 42, 1, "qq", DanmuDownloadFormat.Xml,
            DanmuDownloadDefaults.FileNameTemplate, DownloadConflictPolicy.Rename);

        var result = await service.DownloadAsync(input, "127.0.0.1", 9321, "token", null);

        Assert.Equal(DownloadRecordStatus.Failed, result.Status);
        Assert.Contains("核心实际返回格式", result.ErrorMessage, StringComparison.Ordinal);
        var record = Assert.Single(store.Records);
        Assert.Equal(DownloadRecordStatus.Failed, record.StatusEnum);
    }

    [Fact]
    public async Task DownloadWithoutSaveDirectoryFailsExplicitly()
    {
        var store = new DanmuDownloadStore(_root);
        var service = new DanmuDownloadFileService(store, () => new FakeDownloadClient(SampleXml, null));
        var input = new DanmuDownloadInput(
            string.Empty, "测试番剧", "第一集", 42, 1, "qq", DanmuDownloadFormat.Xml,
            DanmuDownloadDefaults.FileNameTemplate, DownloadConflictPolicy.Rename);

        var result = await service.DownloadAsync(input, "127.0.0.1", 9321, "token", null);

        Assert.Equal(DownloadRecordStatus.Failed, result.Status);
        Assert.Contains("保存目录", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncImportsOnlyRecognizedValidFiles()
    {
        var saveDirectory = Path.Combine(_root, "save", "番剧");
        Directory.CreateDirectory(saveDirectory);
        await File.WriteAllTextAsync(Path.Combine(saveDirectory, "E01.xml"), SampleXml);
        await File.WriteAllTextAsync(Path.Combine(saveDirectory, "notes.txt"), "not danmu");

        var store = new DanmuDownloadStore(_root);
        store.SaveSettings(new DanmuDownloadSettings(SaveDirectory: Path.Combine(_root, "save")));
        var service = new DanmuDownloadFileService(store, () => throw new NotSupportedException());

        var first = await service.SyncExistingFilesAsync();
        Assert.Equal(2, first.ScannedFiles);
        Assert.Equal(1, first.ImportedRecords);
        Assert.Equal(1, first.SkippedFiles);
        var imported = Assert.Single(store.Records);
        Assert.Equal("番剧", imported.AnimeTitle);
        Assert.Equal("目录同步", imported.Source);
        Assert.Equal(DanmuDownloadFormat.Xml, imported.FormatEnum);

        var second = await service.SyncExistingFilesAsync();
        Assert.Equal(0, second.ImportedRecords);
    }

    [Fact]
    public async Task PreviewReadsDownloadedFile()
    {
        var store = new DanmuDownloadStore(_root);
        store.SaveSettings(new DanmuDownloadSettings(SaveDirectory: Path.Combine(_root, "save")));
        var service = new DanmuDownloadFileService(store, () => new FakeDownloadClient(SampleXml, null));
        var input = new DanmuDownloadInput(
            string.Empty, "测试番剧", "第一集", 42, 1, "qq", DanmuDownloadFormat.Xml,
            DanmuDownloadDefaults.FileNameTemplate, DownloadConflictPolicy.Rename);
        await service.DownloadAsync(input, "127.0.0.1", 9321, "token", null);

        var preview = await service.LoadPreviewAsync(store.Records[0]);

        Assert.Equal(2, preview.Count);
        Assert.Equal("hello", preview.Items[0].Text);
    }

    [Fact]
    public async Task RebuildChainResolvesEpisodeFromSearchAndBangumi()
    {
        var store = new DanmuDownloadStore(_root);
        var client = new FakeDownloadClient(SampleXml, null)
        {
            BangumiEpisodes = [new DanmuEpisode("s1", 99, "【qq】第2集 新标题", "2", "", "https://example.invalid/2")],
        };
        var service = new DanmuDownloadFileService(store, () => client);
        var task = new DanmuDownloadTask(
            1, 1, 1, string.Empty, "旧番剧 from qq", "第1集 旧标题", 1000, 2, "qq", "xml",
            DanmuDownloadDefaults.FileNameTemplate, "rename", "pending");

        var input = await service.RebuildChainAsync(task, "127.0.0.1", 9321, "token");

        Assert.Equal(99, input.EpisodeId);
        Assert.Equal("第2集 新标题", input.EpisodeTitle);
        Assert.Equal(9, input.AnimeId);
        Assert.Equal(DanmuDownloadFormat.Xml, input.Format);
    }

    private sealed class FakeDownloadClient(
        string body,
        int? danmuCount,
        string? declaredFormat = null) : IDanmuApiClient
    {
        public IReadOnlyList<DanmuEpisode> BangumiEpisodes { get; set; } = [];

        public Task<DanmuDownloadPayload> DownloadCommentAsync(string host, int port, string? token, long episodeId, string formatValue, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default)
        {
            var payload = new DanmuDownloadPayload(
                200,
                formatValue.Contains("json", StringComparison.Ordinal) ? "application/json" : "application/xml",
                Encoding.UTF8.GetBytes(body),
                declaredFormat ?? formatValue,
                danmuCount);
            return Task.FromResult(payload);
        }

        public Task<DanmuRawApiResponse> SendRawAsync(string host, int port, string? token, string apiKey, IReadOnlyDictionary<string, string?> parameters, string? jsonBody = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DanmuSearchAnimeResult> SearchAnimeAsync(string host, int port, string? token, string keyword, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DanmuSearchAnimeResult(true, [new DanmuAnime(9, "b", "旧番剧", "tv", "TV", "", "", 2, 0, false, "qq", [])]));

        public Task<DanmuSearchEpisodesResult> SearchEpisodesAsync(string host, int port, string? token, string anime, string? episode = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DanmuMatchResult> MatchAsync(string host, int port, string? token, string fileName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DanmuBangumiResult> GetBangumiAsync(string host, int port, string? token, int animeId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DanmuBangumiResult(true, new DanmuBangumi(9, "b", "旧番剧", "", false, 0, false, 0, "tv", "TV", [], BangumiEpisodes)));

        public Task<DanmuResult> GetCommentAsync(string host, int port, string? token, int commentId, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) =>
            throw new NotSupportedException();

        public Task<DanmuResult> GetCommentByUrlAsync(string host, int port, string? token, string videoUrl, bool includeDuration = true, string format = "json", CancellationToken cancellationToken = default, bool segmentFlag = false) =>
            throw new NotSupportedException();

        public Task<DanmuResult> GetSegmentCommentAsync(string host, int port, string? token, JsonElement segment, string format = "json", CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DanmuFavoriteListResult> GetFavoritesAsync(string host, int port, string? token, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> AddFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RemoveFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RefreshFavoriteAsync(string host, int port, string? token, string? adminToken, string keyword, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> SetFavoriteScheduleAsync(string host, int port, string? token, string? adminToken, string keyword, DanmuFavoriteSchedule? schedule, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
