using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// 最近数据（核心 <c>GET /api/cache/animes</c>）的解析与展示层派生逻辑。
/// 解析要求严格：契约字段缺失一律失败，不返回"空列表"让界面看起来正常。
/// </summary>
public sealed class CoreCacheAnimeClientTests
{
    private const string ValidPayload = """
        {
          "success": true,
          "data": [
            {
              "animeTitle": "天气之子(2019)【动漫】from dandan",
              "source": "dandan",
              "imageUrl": "https://example.invalid/cover.jpg",
              "episodes": 2,
              "links": [
                { "url": "dandan:100,bilibili:b01", "title": "【动漫】天气之子 E01" },
                { "url": "dandan:101,bilibili:b02", "title": "【动漫】天气之子 E02" },
                { "url": "dandan:199", "title": "【动漫】天气之子 SP" }
              ],
              "mergedChildren": [
                {
                  "source": "bilibili",
                  "animeId": "x1",
                  "animeTitle": "天气之子【番剧】from bilibili",
                  "imageUrl": "https://example.invalid/child.jpg",
                  "episodes": 2,
                  "links": [
                    { "url": "b01", "title": "【番剧】天气之子 第1话" },
                    { "url": "b02", "title": "【番剧】天气之子 第02话" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void ParsesValidPayload()
    {
        var result = CoreCacheAnimeClient.ParseResponse(Bytes(ValidPayload));

        Assert.True(result.Succeeded, result.Diagnostic);
        var item = Assert.Single(result.Items);
        Assert.Equal("dandan", item.Source);
        Assert.Equal(2, item.Episodes);
        Assert.Equal(3, item.Links.Count);
        var child = Assert.Single(item.MergedChildren);
        Assert.Equal("bilibili", child.Source);
        Assert.Equal(2, child.Episodes);
        Assert.Equal(2, child.Links.Count);
    }

    [Fact]
    public void CleanTitleDropsCoreFromSuffix()
    {
        Assert.Equal("天气之子(2019)【动漫】", CacheAnimePresentation.CleanTitle("天气之子(2019)【动漫】from dandan"));
        Assert.Equal("天气之子", CacheAnimePresentation.CleanTitle("天气之子"));
        Assert.Equal(string.Empty, CacheAnimePresentation.CleanTitle(null));
    }

    [Fact]
    public void CleanOffsetTitleStripsYearBracketsAndTypeSuffix()
    {
        Assert.Equal("天气之子", CacheAnimePresentation.CleanOffsetTitle("天气之子(2019)【动漫】"));
        Assert.Equal("天气之子", CacheAnimePresentation.CleanOffsetTitle("天气之子（2019）"));
        Assert.Equal("天气之子", CacheAnimePresentation.CleanOffsetTitle("天气之子【动漫】"));
        Assert.Equal("百花杀", CacheAnimePresentation.CleanOffsetTitle("\u200B百花杀\u200B(2024)"));
    }

    [Fact]
    public void BuildMappingRowsResolvesChildEpisodeTitlesAndSorts()
    {
        var result = CoreCacheAnimeClient.ParseResponse(Bytes(ValidPayload));
        var parent = result.Items[0];
        var child = parent.MergedChildren[0];

        var rows = CacheAnimePresentation.BuildMappingRows(parent, child);

        // 第 3 条链接只有主源，属于落单，排在匹配行之后。
        Assert.Equal(3, rows.Count);
        Assert.Equal(CacheMappingStatus.Matched, rows[0].Status);
        Assert.Equal(CacheMappingStatus.Matched, rows[1].Status);
        Assert.Equal(CacheMappingStatus.Lonely, rows[2].Status);

        // 副源集数由 child.links 里的可读标题换来，而不是显示原始源 ID。
        Assert.Equal(1, rows[0].ChildEpisodeNumber);
        Assert.Equal($"【dandan】天气之子 E01 ↔ 【bilibili】天气之子 第1话", rows[0].MainSide + " ↔ " + rows[0].ChildSide);

        // 第 3 条链接带主源前缀但没有副源前缀：主源一侧正常，副源标"缺失"。
        Assert.Equal("【dandan】天气之子 SP", rows[2].MainSide);
        Assert.Equal("(副源缺失)", rows[2].ChildSide);
    }

    [Fact]
    public void BuildMappingRowsMarksMainSideOutOfRange()
    {
        // 链接里有 ':' 但不是主源的前缀 → 主源侧算越界。
        var parent = new CacheAnimeEntry(
            "主源",
            "dandan",
            null,
            1,
            [new CacheAnimeLink("youku:5,bilibili:b01", "【动漫】E01")],
            []);
        var child = new CacheAnimeSource("bilibili", "b01", "副源", null, 1, []);

        var row = Assert.Single(CacheAnimePresentation.BuildMappingRows(parent, child));

        Assert.Equal(CacheMappingStatus.Lonely, row.Status);
        Assert.Equal("(主源越界)", row.MainSide);
        Assert.Equal("【bilibili】(源ID: b01)", row.ChildSide);
    }

    [Fact]
    public void BuildMappingRowsFallsBackToSourceIdWhenChildTitleMissing()
    {
        var parent = new CacheAnimeEntry(
            "主源",
            "dandan",
            null,
            1,
            [new CacheAnimeLink("dandan:1,bilibili:zz9", "【动漫】E01")],
            []);
        var child = new CacheAnimeSource("bilibili", "zz9", "副源", null, 1, []);

        var row = Assert.Single(CacheAnimePresentation.BuildMappingRows(parent, child));

        Assert.Equal(CacheMappingStatus.Matched, row.Status);
        Assert.Equal("【bilibili】(源ID: zz9)", row.ChildSide);
        // 与核心一致：副源标题解析不到时退化成 "(源ID: zz9)"，集数就从这串里取到 9。
        Assert.Equal(9, row.ChildEpisodeNumber);
    }

    [Fact]
    public void BuildMappingRowsKeepsCacheOrderWhenChildEpisodeUnknown()
    {
        var parent = new CacheAnimeEntry(
            "主源",
            "dandan",
            null,
            2,
            [
                new CacheAnimeLink("dandan:1,bilibili:b01", "【动漫】E01"),
                new CacheAnimeLink("dandan:2,bilibili:b02", "【动漫】E02"),
            ],
            []);
        // 副源标题里没有数字 → 集数解析不到，应保持缓存原序而不是乱序。
        var child = new CacheAnimeSource(
            "bilibili", "b", "副源", null, 2,
            [new CacheAnimeLink("b01", "第一话"), new CacheAnimeLink("b02", "第二话")]);

        var rows = CacheAnimePresentation.BuildMappingRows(parent, child);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Null(row.ChildEpisodeNumber));
        Assert.Equal([0, 1], rows.Select(row => row.OriginalIndex).ToArray());
    }

    [Fact]
    public void RejectsMissingSuccessFlag()
    {
        var result = CoreCacheAnimeClient.ParseResponse(Bytes("""{ "data": [] }"""));

        Assert.False(result.Succeeded);
        Assert.Contains("success", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsFailureResponse()
    {
        var result = CoreCacheAnimeClient.ParseResponse(Bytes("""{ "success": false, "message": "权限不足" }"""));

        Assert.False(result.Succeeded);
        Assert.Contains("权限不足", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNonArrayData()
    {
        var result = CoreCacheAnimeClient.ParseResponse(Bytes("""{ "success": true, "data": {} }"""));

        Assert.False(result.Succeeded);
        Assert.Contains("data", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsItemWithoutAnimeTitle()
    {
        var result = CoreCacheAnimeClient.ParseResponse(
            Bytes("""{ "success": true, "data": [ { "source": "dandan", "episodes": 1 } ] }"""));

        Assert.False(result.Succeeded);
        Assert.Contains("animeTitle", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsItemWithoutEpisodeCount()
    {
        var result = CoreCacheAnimeClient.ParseResponse(
            Bytes("""{ "success": true, "data": [ { "source": "dandan", "animeTitle": "x" } ] }"""));

        Assert.False(result.Succeeded);
        Assert.Contains("episodes", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsEmptyCache()
    {
        var result = CoreCacheAnimeClient.ParseResponse(Bytes("""{ "success": true, "data": [] }"""));

        Assert.True(result.Succeeded, result.Diagnostic);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void RejectsInvalidJson()
    {
        var result = CoreCacheAnimeClient.ParseResponse(Bytes("not json"));

        Assert.False(result.Succeeded);
        Assert.Contains("JSON", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AnimeEndpointCarriesAdminTokenAsPathPrefix()
    {
        var uri = CoreCacheClient.BuildCacheUri("127.0.0.1", 9321, "runtime-token", "admin-token", "animes");

        Assert.Equal("http://127.0.0.1:9321/admin-token/api/cache/animes", uri.ToString());
    }

    [Fact]
    public void AnimeEndpointFallsBackToRuntimeTokenWithoutAdminSession()
    {
        var uri = CoreCacheClient.BuildCacheUri("127.0.0.1", 9321, "runtime-token", null, "animes");

        Assert.Equal("http://127.0.0.1:9321/runtime-token/api/cache/animes", uri.ToString());
    }

    [Fact]
    public void ClearUriStillBuildsTheSameShape()
    {
        var uri = CoreCacheClient.BuildClearUri("127.0.0.1", 9321, "runtime-token", null);

        Assert.Equal("http://127.0.0.1:9321/runtime-token/api/cache/clear", uri.ToString());
    }
}
