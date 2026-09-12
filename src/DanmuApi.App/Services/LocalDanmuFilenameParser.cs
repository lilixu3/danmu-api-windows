using System.Globalization;
using System.Text.RegularExpressions;

namespace DanmuApi.App.Services;

/// <summary>从文件名猜出来的元数据。所有字段都可被用户在界面上改写。</summary>
public sealed record LocalDanmuFileGuess(
    string Title,
    int? Year,
    string Type,
    int? Season,
    int? Episode,
    IReadOnlyList<string> Notes);

/// <summary>
/// 本地弹幕文件名解析器（对齐移动端 <c>LocalDanmuMetadataResolver</c> 的规则范围，按 Windows 端
/// 自己的下载命名优先）。**只负责给建议**：解析不出来就给 notes，由用户手工确认后再上传。
///
/// 规则优先级：
/// 1. 本应用下载模板 <c>{animeTitle}_E{NN}_{episodeTitle}_{source}.{ext}</c>（置信度最高）；
/// 2. <c>S01E02</c> / <c>S01.E02</c> / <c>1x02</c>；
/// 3. <c>第N季</c> + <c>第M集</c>（含中文数字）；
/// 4. <c>E05</c> / <c>EP05</c>；
/// 5. 年份取方括号/圆括号里的四位数字，其次正文里的四位数字（1900–今年）；
/// 6. 出现「电影 / 剧场版 / 劇場版 / movie」按电影处理，否则默认电视剧。
/// </summary>
public static partial class LocalDanmuFilenameParser
{
    public static LocalDanmuFileGuess Guess(string? fileName, int currentYear)
    {
        var notes = new List<string>();
        var stem = StripDirectoryAndExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return new LocalDanmuFileGuess(string.Empty, null, LocalDanmuTypes.Tv, null, null,
                ["文件名是空的，需要手动填写标题"]);
        }

        var (season, episode, cutIndex) = ExtractSeasonEpisode(stem, notes);
        var year = ExtractYear(stem, currentYear);
        if (year is null)
        {
            notes.Add("未识别到年份，需要手动选择");
        }

        var type = IsMovieName(stem) ? LocalDanmuTypes.Movie : LocalDanmuTypes.Tv;
        if (type == LocalDanmuTypes.Movie)
        {
            notes.Add("文件名里识别到「电影/剧场版」，已按电影处理");
        }

        var (title, strippedSource) = ExtractTitle(stem, cutIndex);
        if (strippedSource)
        {
            notes.Add("已去掉文件名里的来源平台标记（from …）");
        }        if (string.IsNullOrWhiteSpace(title))
        {
            title = stem.Trim();
            notes.Add("未能从文件名切出剧名，请手动确认标题");
        }
        else
        {
            notes.Add("标题来自文件名，请确认");
        }

        if (type == LocalDanmuTypes.Tv)
        {
            if (season is null)
            {
                notes.Add("未识别到季数，默认第 1 季");
            }

            if (episode is null)
            {
                notes.Add("未识别到集数，默认第 1 集");
            }
        }

        return new LocalDanmuFileGuess(title, year, type, season ?? 1, episode, notes);
    }

    /// <summary>去掉目录与扩展名（含 <c>.tar.gz</c> 这类多重后缀里的最后一段）。</summary>
    public static string StripDirectoryAndExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var name = fileName.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static (int? Season, int? Episode, int CutIndex) ExtractSeasonEpisode(string stem, List<string> notes)
    {
        var template = DownloadTemplatePattern().Match(stem);
        var standard = SeasonEpisodePattern().Match(stem);
        var crossed = CrossPattern().Match(stem);
        var chineseSeason = ChineseSeasonPattern().Match(stem);
        var chineseEpisode = ChineseEpisodePattern().Match(stem);
        var seasonWord = SeasonWordPattern().Match(stem);
        var bare = BareEpisodePattern().Match(stem);
        var loose = LooseEpisodePattern().Match(stem);

        // 数值按优先级取：下载模板 → SxxExx → 第N季/第M集 → NxM → Exx → 裸数字。
        int? season = null;
        int? episode = null;
        if (template.Success && template.Index > 0)
        {
            season = template.Groups["season"].Success ? ParseNumber(template.Groups["season"].Value) : null;
            episode = ParseNumber(template.Groups["episode"].Value);
        }
        else if (standard.Success)
        {
            season = ParseNumber(standard.Groups["season"].Value);
            episode = ParseNumber(standard.Groups["episode"].Value);
        }
        else if (chineseSeason.Success || chineseEpisode.Success)
        {
            season = chineseSeason.Success ? ParseChineseNumber(chineseSeason.Groups["value"].Value) : null;
            episode = chineseEpisode.Success ? ParseChineseNumber(chineseEpisode.Groups["value"].Value) : null;
        }
        else if (crossed.Success)
        {
            season = ParseNumber(crossed.Groups["season"].Value);
            episode = ParseNumber(crossed.Groups["episode"].Value);
        }
        else if (bare.Success)
        {
            episode = ParseNumber(bare.Groups["episode"].Value);
        }
        else if (loose.Success)
        {
            notes.Add("集数来自文件名里的裸数字，请确认");
            episode = ParseNumber(loose.Groups["episode"].Value);
        }
        else
        {
            notes.Add("文件名里没有集数信息");
            return (null, null, -1);
        }

        // 切分点取「所有识别到的最早 token」，而不是取值分支的位置：
        // 例如「逐玉_EP12_第12集」取值走的是中文分支，但剧名必须切在 _EP12 之前。
        var cutIndex = -1;
        foreach (var match in new[] { template, standard, crossed, chineseSeason, chineseEpisode, seasonWord, bare, loose })
        {
            if (match.Success && (cutIndex < 0 || match.Index < cutIndex))
            {
                cutIndex = match.Index;
            }
        }

        // 本应用下载模板只带集号，季往往写在标题里（核心 animeTitle 的「第 N 季」）——补上它，
        // 否则「凡人修仙传 第2季…」会被当成第 1 季。
        season ??= chineseSeason.Success
            ? ParseChineseNumber(chineseSeason.Groups["value"].Value)
            : standard.Success
                ? ParseNumber(standard.Groups["season"].Value)
                : seasonWord.Success
                    ? ParseNumber(seasonWord.Groups["season"].Value)
                    : null;

        return (season, episode, cutIndex);
    }

    private static int? ExtractYear(string stem, int currentYear)
    {
        foreach (Match match in BracketedYearPattern().Matches(stem))
        {
            if (InRange(match.Groups["year"].Value))
            {
                return int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            }
        }

        foreach (Match match in YearPattern().Matches(stem))
        {
            var value = match.Groups["year"].Value;
            if (InRange(value))
            {
                return int.Parse(value, CultureInfo.InvariantCulture);
            }
        }

        return null;

        bool InRange(string value) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
            year >= 1900 && year <= currentYear;
    }

    /// <summary>把剧名候选清理成真实剧名；同时回报是否真的去掉了「from 平台」标记（供 notes 用）。</summary>
    private static (string Title, bool StrippedSource) ExtractTitle(string stem, int cutIndex)
    {
        var candidate = cutIndex > 0 ? stem[..cutIndex] : stem;
        // 去掉发布组前缀 [Lilith-Raws] 与其它技术性括号组（[1080p]、【GB】、(2024) 等）。
        candidate = LeadingBracketGroupPattern().Replace(candidate, " ");
        candidate = BracketGroupPattern().Replace(candidate, " ");
        // 核心返回的 animeTitle 形如「凡人修仙传 第1季(2026)【TV】from 腾讯」：
        // 季、年份、类型、来源都是元数据而不是剧名（用户实测反馈：剧名别带 from 平台）。
        // 判断放在括号清理之后：`from 腾讯 (2026)` 这种顺序只有去括号才看得出结尾是平台名。
        var strippedSource = false;
        if (TrailingFromSourcePattern().IsMatch(candidate))
        {
            candidate = TrailingFromSourcePattern().Replace(candidate, string.Empty);
            strippedSource = true;
        }

        candidate = SeasonTokenPattern().Replace(candidate, " ");
        candidate = SeasonWordPattern().Replace(candidate, " ");
        candidate = TrailingYearPattern().Replace(candidate, string.Empty);
        candidate = SeparatorTailPattern().Replace(candidate, string.Empty);
        // 点号/下划线归一化成空格：核心的 normalizeLocalKey 会把标题里最后一个「点号后缀」
        // 当扩展名丢掉（Show.Name → show），留着点号会让资源键丢字。
        candidate = InlineSeparatorPattern().Replace(candidate, " ");
        return (InlineWhitespacePattern().Replace(candidate, " ").Trim(), strippedSource);
    }

    private static bool IsMovieName(string stem) => MovieNamePattern().IsMatch(stem);

    private static int? ParseNumber(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;

    /// <summary>中文数字（一到九百九十九）转整数；纯阿拉伯数字直接用。</summary>
    internal static int? ParseChineseNumber(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var arabic))
        {
            return arabic > 0 ? arabic : null;
        }

        var digits = new Dictionary<char, int>
        {
            ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4,
            ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
        };
        var total = 0;
        var current = 0;
        foreach (var character in text)
        {
            switch (character)
            {
                case '十':
                    current = current == 0 ? 1 : current;
                    total += current * 10;
                    current = 0;
                    break;
                case '百':
                    current = current == 0 ? 1 : current;
                    total += current * 100;
                    current = 0;
                    break;
                default:
                    if (!digits.TryGetValue(character, out var digit))
                    {
                        return null;
                    }

                    current = digit;
                    break;
            }
        }

        total += current;
        return total > 0 ? total : null;
    }

    [GeneratedRegex(@"_(?:S(?<season>\d{1,2}))?E(?<episode>\d{1,4})(?:_|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadTemplatePattern();

    [GeneratedRegex(@"[Ss](?<season>\d{1,2})[\s._-]?[Ee][Pp]?(?<episode>\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodePattern();

    [GeneratedRegex(@"第\s*(?<value>[0-9]{1,3}|[零〇一二两三四五六七八九十百]{1,4})\s*[季部]", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseSeasonPattern();

    [GeneratedRegex(@"第\s*(?<value>[0-9]{1,4}|[零〇一二两三四五六七八九十百]{1,4})\s*[集话話]", RegexOptions.CultureInvariant)]
    private static partial Regex ChineseEpisodePattern();

    [GeneratedRegex(@"(?<season>\d{1,2})\s*[xX]\s*(?<episode>\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex CrossPattern();

    [GeneratedRegex(@"(?:^|[\s._-])[Ee][Pp]?(?<episode>\d{1,4})(?:[\s._-]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BareEpisodePattern();

    [GeneratedRegex(@"[\[\(【]\s*(?<year>(?:19|20)\d{2})\s*[\]\)】]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedYearPattern();

    [GeneratedRegex(@"(?<year>(?:19|20)\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();

    /// <summary>发布组命名的裸集号。限定 1–3 位且两侧是分隔符，并排除「Season 2」「第 2」里的数字——
    /// 那几个是季数，不能被当成集数（否则 "From Season 2" 会被切成剧名 "From Season"）。</summary>
    [GeneratedRegex(@"(?<![Ss]eason)(?<!第)(?:^|[\s._\-\[])(?<episode>\d{1,3})(?:[\s._\-\]]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LooseEpisodePattern();

    [GeneratedRegex(@"^\s*(?:[\[\(【][^\]\)】]*[\]\)】]\s*)+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingBracketGroupPattern();

    [GeneratedRegex(@"(?<year>(?:19|20)\d{2})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYearPattern();

    [GeneratedRegex(@"[._]+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSeparatorPattern();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex InlineWhitespacePattern();

    [GeneratedRegex(@"[\[\(【][^\]\)】]*[\]\)】]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroupPattern();

    [GeneratedRegex(@"\s+from\s+\S+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingFromSourcePattern();

    [GeneratedRegex(@"第\s*(?:[0-9]{1,3}|[零〇一二两三四五六七八九十百]{1,4})\s*[季部]", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonTokenPattern();

    [GeneratedRegex(@"\bSeason\s*(?<season>[0-9]{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeasonWordPattern();

    [GeneratedRegex(@"[\s._\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorTailPattern();

    [GeneratedRegex(@"(电影|剧场版|劇場版|movie)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MovieNamePattern();
}

/// <summary>核心只接受这两种类型（<c>normalizeLocalType</c> 认识更多，但 upload 会拒）。</summary>
public static class LocalDanmuTypes
{
    public const string Tv = "tv";
    public const string Movie = "movie";

    public static string ToLabel(string type) => type == Movie ? "电影" : "电视剧";
}

/// <summary>
/// 上传前校验。文案与核心 <c>handleLocalDanmuUpload</c> 的报错逐字对齐，
/// 这样界面本地校验与核心返回的 400 提示不会出现两套说法。
/// </summary>
public static class LocalDanmuValidation
{
    public const int MinimumYear = 1900;
    public const long MaximumFileBytes = 10L * 1024 * 1024;

    public static string? ValidateTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "标题为必填项" : null;

    public static string? ValidateYear(int? year, int currentYear)
    {
        if (year is null)
        {
            return "年份为必填项";
        }

        if (year is < MinimumYear or > 9999 || year > currentYear)
        {
            return $"年份必须在 {MinimumYear}–{currentYear} 年之间";
        }

        return null;
    }

    public static string? ValidateType(string? type) =>
        string.IsNullOrWhiteSpace(type)
            ? "类型为必填项"
            : type is not (LocalDanmuTypes.Tv or LocalDanmuTypes.Movie)
                ? "类型只能选择 tv 或 movie"
                : null;

    public static string? ValidateSeason(int? season) =>
        season is null or <= 0 ? "季数必须是大于 0 的整数" : null;

    public static string? ValidateEpisode(int? episode, bool isMovie)
    {
        if (episode is null)
        {
            return isMovie ? null : "集数必须是大于 0 的整数";
        }

        return episode <= 0 ? "集数必须是大于 0 的整数" : null;
    }

    public static string? ValidateFileSize(long? bytes) =>
        bytes is > MaximumFileBytes ? "单文件不能超过 10 MB" : null;
}
