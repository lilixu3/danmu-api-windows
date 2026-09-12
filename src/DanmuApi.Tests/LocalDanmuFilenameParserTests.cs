using DanmuApi.App.Services;

namespace DanmuApi.Tests;

/// <summary>
/// 文件名解析器与上传校验：解析只给建议（不确定的地方必须给 notes），校验文案与核心
/// <c>handleLocalDanmuUpload</c> 的报错逐字一致，避免界面两套说法。
/// </summary>
public sealed class LocalDanmuFilenameParserTests
{
    private const int CurrentYear = 2026;

    [Theory]
    // 本应用下载模板：剧名_E集_集名_来源
    [InlineData("逐玉_E05_第5集_腾讯.xml", "逐玉", null, "tv", 5, 1)]
    // SxxExx 标准命名
    [InlineData("Show.Name.S01E02.1080p.WEB-DL.x264-GROUP.mkv", "Show Name", null, "tv", 2, 1)]
    [InlineData("剧名 S02E11 [WEB-DL].mp4", "剧名", null, "tv", 11, 2)]
    // 中文季集
    [InlineData("逐玉 第二季 第11集 2160p.mp4", "逐玉", null, "tv", 11, 2)]
    // 4 位年份（方括号优先）
    [InlineData("逐玉 (2026) S01E03.mkv", "逐玉", 2026, "tv", 3, 1)]
    [InlineData("逐玉 2025 第7集.mp4", "逐玉", 2025, "tv", 7, 1)]
    // 发布组裸集号
    [InlineData("[Lilith-Raws] 逐玉 - 05 [Baha][WEB-DL][1080p].mp4", "逐玉", null, "tv", 5, 1)]
    // 电影（季集可空）
    [InlineData("某剧场版 2024.mkv", "某剧场版", 2024, "movie", null, 1)]
    // 文件名里带 Movie 也算电影信号；没有集数信息时保持 tv 并给说明
    [InlineData("Some Show (2023).mp4", "Some Show", 2023, "tv", null, 1)]
    // Exx
    [InlineData("逐玉_EP12_第12集.xml", "逐玉", null, "tv", 12, 1)]
    // 核心返回的 animeTitle 形如「剧名 第N季(年)【类型】from 平台」——剧名只保留主体（用户实测反馈）
    [InlineData("凡人修仙传 第1季(2026)【TV】from 腾讯_E05_第5集_腾讯.xml", "凡人修仙传", 2026, "tv", 5, 1)]
    [InlineData("凡人修仙传 第2季(2026)【TV】from local_E07_第7集_local.xml", "凡人修仙传", 2026, "tv", 7, 2)]
    [InlineData("某剧 中国 第3季(2025) from 360_E01_第1集_360.xml", "某剧 中国", 2025, "tv", 1, 3)]
    public void GuessExtractsMetadata(
        string fileName,
        string expectedTitle,
        int? expectedYear,
        string expectedType,
        int? expectedEpisode,
        int expectedSeason)
    {
        var guess = LocalDanmuFilenameParser.Guess(fileName, CurrentYear);

        Assert.Equal(expectedTitle, guess.Title);
        Assert.Equal(expectedYear, guess.Year);
        Assert.Equal(expectedType, guess.Type);
        Assert.Equal(expectedEpisode, guess.Episode);
        Assert.Equal(expectedSeason, guess.Season);
    }

    [Fact]
    public void GuessDropsPlatformSuffixAndExplainsIt()
    {
        // 核心 animeTitle + 本应用下载模板：切分点在「第1季」上，from 平台顺带被切掉。
        var withSeasonToken = LocalDanmuFilenameParser.Guess(
            "凡人修仙传 第1季(2026)【TV】from 腾讯_E05_第5集_腾讯.xml",
            CurrentYear);
        Assert.Equal("凡人修仙传", withSeasonToken.Title);

        // 没有季标记时，from 平台会落在剧名候选里，必须被专门去掉并给出说明。
        var withSourceInTitle = LocalDanmuFilenameParser.Guess("凡人修仙传 from 腾讯 (2026) E05.xml", CurrentYear);
        Assert.Equal("凡人修仙传", withSourceInTitle.Title);
        Assert.Contains(withSourceInTitle.Notes, note => note.Contains("来源平台标记", StringComparison.Ordinal));

        foreach (var guess in new[] { withSeasonToken, withSourceInTitle })
        {
            Assert.DoesNotContain("from", guess.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("腾讯", guess.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("第1季", guess.Title, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Escape from New York (2024).mkv", "Escape from New York")]
    [InlineData("From Season 2 (2025).mkv", "From")]
    public void GuessKeepsTitlesThatLegitimatelyContainFrom(string fileName, string expectedTitle)
    {
        var guess = LocalDanmuFilenameParser.Guess(fileName, CurrentYear);

        Assert.Equal(expectedTitle, guess.Title);
    }

    [Fact]
    public void GuessAlwaysExplainsWhatItCouldNotDetermine()
    {
        var guess = LocalDanmuFilenameParser.Guess("逐玉_E05_第5集_腾讯.xml", CurrentYear);

        Assert.Contains("未识别到年份，需要手动选择", guess.Notes);
        Assert.Contains("标题来自文件名，请确认", guess.Notes);
        // 季从模板里拿不到，必须明确告诉用户默认值。
        Assert.Contains("未识别到季数，默认第 1 季", guess.Notes);
    }

    [Fact]
    public void GuessFlagsMoviesWhenTheNameSaysSo()
    {
        var guess = LocalDanmuFilenameParser.Guess("逐玉 剧场版 2026.mkv", CurrentYear);

        Assert.Equal("movie", guess.Type);
        Assert.Contains(guess.Notes, note => note.Contains("电影", StringComparison.Ordinal));
    }

    [Fact]
    public void GuessHandlesEmptyNames()
    {
        var guess = LocalDanmuFilenameParser.Guess("   ", CurrentYear);

        Assert.Equal(string.Empty, guess.Title);
        Assert.Null(guess.Year);
        Assert.Contains(guess.Notes, note => note.Contains("文件名是空的", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("五", 5)]
    [InlineData("十一", 11)]
    [InlineData("二十三", 23)]
    [InlineData("一百零五", 105)]
    [InlineData("12", 12)]
    [InlineData("abc", null)]
    public void ChineseNumbersAreParsed(string value, int? expected) =>
        Assert.Equal(expected, LocalDanmuFilenameParser.ParseChineseNumber(value));

    [Fact]
    public void StripDirectoryAndExtensionDropsFoldersAndSuffix()
    {
        Assert.Equal("逐玉_E05", LocalDanmuFilenameParser.StripDirectoryAndExtension(@"D:\弹幕\逐玉_E05.xml"));
        Assert.Equal("逐玉_E05", LocalDanmuFilenameParser.StripDirectoryAndExtension("逐玉_E05.xml"));
        Assert.Equal("no-extension", LocalDanmuFilenameParser.StripDirectoryAndExtension("no-extension"));
        Assert.Equal(string.Empty, LocalDanmuFilenameParser.StripDirectoryAndExtension(null));
    }

    [Fact]
    public void ValidationMessagesMatchTheCoreWording()
    {
        Assert.Equal("标题为必填项", LocalDanmuValidation.ValidateTitle("  "));
        Assert.Null(LocalDanmuValidation.ValidateTitle("逐玉"));

        Assert.Equal("年份为必填项", LocalDanmuValidation.ValidateYear(null, CurrentYear));
        Assert.Equal($"年份必须在 1900–{CurrentYear} 年之间", LocalDanmuValidation.ValidateYear(1899, CurrentYear));
        Assert.Equal($"年份必须在 1900–{CurrentYear} 年之间", LocalDanmuValidation.ValidateYear(CurrentYear + 1, CurrentYear));
        Assert.Null(LocalDanmuValidation.ValidateYear(2026, CurrentYear));

        Assert.Equal("类型为必填项", LocalDanmuValidation.ValidateType(""));
        Assert.Equal("类型只能选择 tv 或 movie", LocalDanmuValidation.ValidateType("ova"));
        Assert.Null(LocalDanmuValidation.ValidateType("movie"));

        Assert.Equal("季数必须是大于 0 的整数", LocalDanmuValidation.ValidateSeason(0));
        Assert.Null(LocalDanmuValidation.ValidateSeason(1));

        Assert.Equal("集数必须是大于 0 的整数", LocalDanmuValidation.ValidateEpisode(0, isMovie: false));
        Assert.Equal("集数必须是大于 0 的整数", LocalDanmuValidation.ValidateEpisode(null, isMovie: false));
        Assert.Null(LocalDanmuValidation.ValidateEpisode(null, isMovie: true));
        Assert.Null(LocalDanmuValidation.ValidateEpisode(3, isMovie: false));

        Assert.Equal("单文件不能超过 10 MB", LocalDanmuValidation.ValidateFileSize(10L * 1024 * 1024 + 1));
        Assert.Null(LocalDanmuValidation.ValidateFileSize(10L * 1024 * 1024));
    }
}
