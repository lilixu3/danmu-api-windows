using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DanmuApi.Core;

namespace DanmuApi.Runtime;

public sealed record MergeSourceGroup(string Primary, IReadOnlyList<string> Secondaries);

public sealed record CustomMergeEntity(
    string Title,
    int? Season,
    IReadOnlyList<string> Sources);

public sealed record EpisodeRange(int Start, int End);

public sealed record EpisodeRoute(EpisodeRange Secondary, EpisodeRange Primary);

public sealed record CustomMergeRule(
    CustomMergeEntity Secondary,
    bool IsBlocked,
    CustomMergeEntity Primary,
    IReadOnlyList<EpisodeRoute> Routes);

public sealed record DanmuOffsetRule(
    string Title,
    int? Season,
    int? Episode,
    IReadOnlyList<string> Sources,
    bool AllSources,
    bool UsePercent,
    decimal Seconds);

public enum IpBlacklistEntryType
{
    Address,
    Cidr,
    RegularExpression,
}

public sealed record IpBlacklistEntry(IpBlacklistEntryType Type, string Value);

public sealed record CoreGradientPalette(string? Skin, IReadOnlyList<uint> Colors);

public sealed record AutoMatchMappingRule(
    string SourceTitle,
    int SourceSeason,
    int SourceStartEpisode,
    int? SourceEndEpisode,
    string TargetDisplayTitle,
    int TargetSeason,
    int TargetStartEpisode,
    int? TargetEndEpisode,
    string? TargetPlatform);

public static class CoreEnvStructuredValues
{
    public static IReadOnlyList<string> GradientSkins { get; } =
        ["bilibili", "sweet", "cyber", "sunset", "ocean", "mint", "rainbow"];

    private static readonly Regex CustomMergeEntityPattern = new(
        @"^(?<title>.+?)(?:/S(?<season>\d+))?@(?<sources>[A-Za-z0-9_&]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex EpisodeRangePattern = new(
        @"^E(?<start>\d+)(?:~E(?<end>\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DanmuPathPattern = new(
        @"^(?<title>.+?)(?:/S(?<season>\d+))?(?:/E(?<episode>\d+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AutoMatchEpisodeSidePattern = new(
        @"^(?<title>.+?)\s+S(?<season>\d+)E(?<start>\d+)(?:~E?(?<end>\d+))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AutoMatchPlatformPattern = new(
        @"\s+@(?<platform>[A-Za-z0-9_-]+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AutoMatchTargetYearPattern = new(
        @"[（(](?:19|20)\d{2}[)）]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AutoMatchTargetTypePattern = new(
        @"【[^】]+】",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static IReadOnlyList<MergeSourceGroup> ParseMergeSourcePairs(
        CoreEnvDefinition definition,
        string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return [];
        }

        var groups = new List<MergeSourceGroup>();
        foreach (var rawGroup in value.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var sources = rawGroup.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (sources.Length == 0)
            {
                throw new FormatException("MERGE_SOURCE_PAIRS 包含空的来源组");
            }

            EnsureKnownSources(definition.Options, sources, "MERGE_SOURCE_PAIRS");
            EnsureDistinct(sources, $"MERGE_SOURCE_PAIRS 来源组 {rawGroup}");
            groups.Add(new MergeSourceGroup(sources[0], sources[1..]));
        }

        return groups;
    }

    public static string FormatMergeSourcePairs(IEnumerable<MergeSourceGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return string.Join(',', groups.Select(group =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(group.Primary);
            var values = new[] { group.Primary.Trim() }
                .Concat(group.Secondaries.Select(value => value.Trim()))
                .ToArray();
            EnsureDistinct(values, $"MERGE_SOURCE_PAIRS 来源组 {group.Primary}");
            return string.Join('&', values);
        }));
    }

    public static IReadOnlyList<CustomMergeRule> ParseCustomMergeRules(
        CoreEnvDefinition definition,
        string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return [];
        }

        var rules = new List<CustomMergeRule>();
        foreach (var rawRule in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pipe = rawRule.IndexOf('|');
            if (pipe >= 0 && rawRule.IndexOf('|', pipe + 1) >= 0)
            {
                throw new FormatException($"CUSTOM_MERGE_RULES 包含多个路由分隔符：{rawRule}");
            }

            var entities = pipe >= 0 ? rawRule[..pipe].Trim() : rawRule;
            var routesText = pipe >= 0 ? rawRule[(pipe + 1)..].Trim() : string.Empty;
            var arrow = entities.IndexOf("->", StringComparison.Ordinal);
            var block = entities.IndexOf('×');
            if ((arrow >= 0) == (block >= 0))
            {
                throw new FormatException($"CUSTOM_MERGE_RULES 必须且只能包含一种关系：{rawRule}");
            }

            var separator = arrow >= 0 ? "->" : "×";
            var separatorIndex = arrow >= 0 ? arrow : block;
            if (entities.IndexOf(separator, separatorIndex + separator.Length, StringComparison.Ordinal) >= 0)
            {
                throw new FormatException($"CUSTOM_MERGE_RULES 关系分隔符重复：{rawRule}");
            }

            var secondaryText = entities[..separatorIndex].Trim();
            var primaryText = entities[(separatorIndex + separator.Length)..].Trim();
            var secondary = ParseCustomMergeEntity(definition, secondaryText, rawRule);
            var primary = ParseCustomMergeEntity(definition, primaryText, rawRule);
            var isBlocked = block >= 0;
            if (isBlocked && routesText.Length > 0)
            {
                throw new FormatException($"CUSTOM_MERGE_RULES 阻断规则不能配置集数路由：{rawRule}");
            }

            var routes = routesText.Length == 0
                ? []
                : ParseEpisodeRoutes(routesText, rawRule);
            rules.Add(new CustomMergeRule(secondary, isBlocked, primary, routes));
        }

        return rules;
    }

    public static IReadOnlyList<AutoMatchMappingRule> ParseAutoMatchMappings(
        CoreEnvDefinition definition,
        string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return [];
        }

        var rules = new List<AutoMatchMappingRule>();
        foreach (var rawRule in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawRule.IndexOf("->", StringComparison.Ordinal);
            if (separator <= 0 || separator + 2 >= rawRule.Length ||
                rawRule.IndexOf("->", separator + 2, StringComparison.Ordinal) >= 0)
            {
                throw new FormatException($"AUTO_MATCH_MAPPING_TABLE 必须且只能包含一个映射分隔符：{rawRule}");
            }

            var source = ParseAutoMatchEpisodeSide(rawRule[..separator].Trim(), rawRule);
            var targetText = rawRule[(separator + 2)..].Trim();
            string? platform = null;
            var platformMatch = AutoMatchPlatformPattern.Match(targetText);
            if (platformMatch.Success)
            {
                platform = platformMatch.Groups["platform"].Value.ToLowerInvariant();
                targetText = targetText[..platformMatch.Index].TrimEnd();
                EnsureKnownSources(definition.Sources, [platform], "AUTO_MATCH_MAPPING_TABLE");
            }

            var target = ParseAutoMatchEpisodeSide(targetText, rawRule);
            var sourceBounded = source.EndEpisode is not null;
            var targetBounded = target.EndEpisode is not null;
            if (sourceBounded != targetBounded)
            {
                throw new FormatException($"AUTO_MATCH_MAPPING_TABLE 源和目标必须同时声明集数范围：{rawRule}");
            }

            if (sourceBounded &&
                source.EndEpisode!.Value - source.StartEpisode != target.EndEpisode!.Value - target.StartEpisode)
            {
                throw new FormatException($"AUTO_MATCH_MAPPING_TABLE 源和目标的集数范围长度必须一致：{rawRule}");
            }

            var targetTitle = AutoMatchTargetTypePattern.Replace(
                AutoMatchTargetYearPattern.Replace(target.Title, string.Empty),
                string.Empty).Trim();
            if (targetTitle.Length == 0)
            {
                throw new FormatException($"AUTO_MATCH_MAPPING_TABLE 目标标题不能为空：{rawRule}");
            }

            rules.Add(new AutoMatchMappingRule(
                source.Title,
                source.Season,
                source.StartEpisode,
                source.EndEpisode,
                target.Title,
                target.Season,
                target.StartEpisode,
                target.EndEpisode,
                platform));
        }

        return rules;
    }

    public static string FormatAutoMatchMappings(
        CoreEnvDefinition definition,
        IEnumerable<AutoMatchMappingRule> rules)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(rules);
        var value = string.Join(';', rules.Select(rule =>
        {
            var source = FormatAutoMatchEpisodeSide(
                rule.SourceTitle,
                rule.SourceSeason,
                rule.SourceStartEpisode,
                rule.SourceEndEpisode);
            var target = FormatAutoMatchEpisodeSide(
                rule.TargetDisplayTitle,
                rule.TargetSeason,
                rule.TargetStartEpisode,
                rule.TargetEndEpisode);
            if (!string.IsNullOrWhiteSpace(rule.TargetPlatform))
            {
                target += $" @{rule.TargetPlatform.Trim().ToLowerInvariant()}";
            }

            return $"{source} -> {target}";
        }));
        _ = ParseAutoMatchMappings(definition, value);
        return value;
    }

    public static string FormatCustomMergeRules(IEnumerable<CustomMergeRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return string.Join(';', rules.Select(rule =>
        {
            if (rule.IsBlocked && rule.Routes.Count > 0)
            {
                throw new FormatException("CUSTOM_MERGE_RULES 阻断规则不能配置集数路由");
            }

            var value = $"{FormatCustomMergeEntity(rule.Secondary)} {(rule.IsBlocked ? "×" : "->")} {FormatCustomMergeEntity(rule.Primary)}";
            if (rule.Routes.Count > 0)
            {
                value += " | " + string.Join(',', rule.Routes.Select(FormatEpisodeRoute));
            }

            return value;
        }));
    }

    public static IReadOnlyList<DanmuOffsetRule> ParseDanmuOffsets(
        CoreEnvDefinition definition,
        string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return [];
        }

        var rules = new List<DanmuOffsetRule>();
        foreach (var rawRule in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = rawRule.LastIndexOf(':');
            if (colon <= 0 || colon == rawRule.Length - 1)
            {
                throw new FormatException($"DANMU_OFFSET 规则缺少有效偏移秒数：{rawRule}");
            }

            var path = rawRule[..colon].Trim();
            var secondsText = rawRule[(colon + 1)..].Trim();
            if (!decimal.TryParse(secondsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                throw new FormatException($"DANMU_OFFSET 偏移秒数非法：{secondsText}");
            }

            var usePercent = path.EndsWith('%');
            if (usePercent)
            {
                path = path[..^1].TrimEnd();
            }

            var sources = Array.Empty<string>();
            var allSources = false;
            var at = path.LastIndexOf('@');
            if (at >= 0)
            {
                var sourcesText = path[(at + 1)..].Trim();
                path = path[..at].TrimEnd();
                if (sourcesText is "all" or "*")
                {
                    allSources = true;
                }
                else
                {
                    sources = sourcesText.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (sources.Length == 0)
                    {
                        throw new FormatException($"DANMU_OFFSET 来源列表为空：{rawRule}");
                    }

                    EnsureKnownSources(definition.Sources, sources, "DANMU_OFFSET");
                    EnsureDistinct(sources, $"DANMU_OFFSET 规则 {rawRule}");
                }
            }

            var match = DanmuPathPattern.Match(path);
            if (!match.Success)
            {
                throw new FormatException($"DANMU_OFFSET 路径格式错误：{path}");
            }

            var title = match.Groups["title"].Value.Trim();
            if (title.Length == 0)
            {
                throw new FormatException($"DANMU_OFFSET 剧名不能为空：{rawRule}");
            }

            var season = ParsePositiveOptional(match.Groups["season"], "DANMU_OFFSET 季数");
            var episode = ParsePositiveOptional(match.Groups["episode"], "DANMU_OFFSET 集数");
            if (episode is not null && season is null)
            {
                throw new FormatException($"DANMU_OFFSET 指定集数时必须同时指定季数：{rawRule}");
            }

            rules.Add(new DanmuOffsetRule(title, season, episode, sources, allSources, usePercent, seconds));
        }

        return rules;
    }

    public static string FormatDanmuOffsets(IEnumerable<DanmuOffsetRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return string.Join(',', rules.Select(rule =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.Title);
            if (rule.Season is <= 0 || rule.Episode is <= 0)
            {
                throw new FormatException("DANMU_OFFSET 季数和集数必须是正整数");
            }

            if (rule.Episode is not null && rule.Season is null)
            {
                throw new FormatException("DANMU_OFFSET 指定集数时必须同时指定季数");
            }

            if (rule.AllSources && rule.Sources.Count > 0)
            {
                throw new FormatException("DANMU_OFFSET 不能同时指定全部来源和具体来源");
            }

            var path = rule.Title.Trim();
            if (rule.Season is int season)
            {
                path += $"/S{season:00}";
            }

            if (rule.Episode is int episode)
            {
                path += $"/E{episode:00}";
            }

            if (rule.AllSources)
            {
                path += "@all";
            }
            else if (rule.Sources.Count > 0)
            {
                path += "@" + string.Join('&', rule.Sources);
            }

            if (rule.UsePercent)
            {
                path += '%';
            }

            return $"{path}:{rule.Seconds.ToString("G29", CultureInfo.InvariantCulture)}";
        }));
    }

    public static IReadOnlyList<uint> ParseColorPool(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return [];
        }

        var colors = new List<uint>();
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!uint.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out var color) || color > 0xFFFFFF)
            {
                throw new FormatException($"颜色值非法：{item}");
            }

            colors.Add(color);
        }

        return colors;
    }

    public static string FormatColorPool(IEnumerable<uint> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        return string.Join(',', colors.Select(color =>
        {
            if (color > 0xFFFFFF)
            {
                throw new FormatException($"颜色值超出 RGB 范围：{color}");
            }

            return color.ToString(CultureInfo.InvariantCulture);
        }));
    }

    public static uint ParseHexColor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.StartsWith('#'))
        {
            normalized = normalized[1..];
        }

        if (normalized.Length != 6 ||
            !uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
        {
            throw new FormatException($"颜色必须是 6 位十六进制 RGB，例如 #FF6600：{value}");
        }

        return color;
    }

    public static string FormatHexColor(uint color)
    {
        if (color > 0xFFFFFF)
        {
            throw new FormatException($"颜色值超出 RGB 范围：{color}");
        }

        return $"#{color:X6}";
    }

    public static CoreGradientPalette ParseGradientPalette(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return new CoreGradientPalette(null, []);
        }

        var normalized = value.Trim();
        if (GradientSkins.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return new CoreGradientPalette(normalized.ToLowerInvariant(), []);
        }

        var colors = ParseColorPool(normalized);
        if (colors.Count < 2)
        {
            throw new FormatException("GRADIENT_COLORS 自定义渐变至少需要两个颜色值");
        }

        return new CoreGradientPalette(null, colors);
    }

    public static string FormatGradientPalette(CoreGradientPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (!string.IsNullOrWhiteSpace(palette.Skin))
        {
            var skin = palette.Skin.Trim().ToLowerInvariant();
            if (!GradientSkins.Contains(skin, StringComparer.Ordinal))
            {
                throw new FormatException($"GRADIENT_COLORS 皮肤名非法：{palette.Skin}");
            }

            if (palette.Colors.Count > 0)
            {
                throw new FormatException("GRADIENT_COLORS 不能同时使用预设皮肤和自定义颜色");
            }

            return skin;
        }

        if (palette.Colors.Count < 2)
        {
            throw new FormatException("GRADIENT_COLORS 自定义渐变至少需要两个颜色值");
        }

        return FormatColorPool(palette.Colors);
    }

    public static IReadOnlyList<IpBlacklistEntry> ParseIpBlacklist(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return [];
        }

        var entries = new List<IpBlacklistEntry>();
        foreach (var rawEntry in value.Split([',', ';', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawEntry.StartsWith('/') && rawEntry.LastIndexOf('/') > 0)
            {
                ValidateRegularExpression(rawEntry);
                entries.Add(new IpBlacklistEntry(IpBlacklistEntryType.RegularExpression, rawEntry));
                continue;
            }

            var slash = rawEntry.LastIndexOf('/');
            if (slash > 0)
            {
                var addressText = rawEntry[..slash];
                var prefixText = rawEntry[(slash + 1)..];
                if (!IPAddress.TryParse(addressText, out var address) ||
                    !int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
                    prefix < 0 ||
                    prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
                {
                    throw new FormatException($"IP_BLACKLIST CIDR 非法：{rawEntry}");
                }

                entries.Add(new IpBlacklistEntry(IpBlacklistEntryType.Cidr, rawEntry));
                continue;
            }

            if (!IPAddress.TryParse(rawEntry, out _))
            {
                throw new FormatException($"IP_BLACKLIST 地址或规则非法：{rawEntry}");
            }

            entries.Add(new IpBlacklistEntry(IpBlacklistEntryType.Address, rawEntry));
        }

        return entries;
    }

    public static string FormatIpBlacklist(IEnumerable<IpBlacklistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return string.Join(',', entries.Select(entry => entry.Value.Trim()));
    }

    private static CustomMergeEntity ParseCustomMergeEntity(
        CoreEnvDefinition definition,
        string value,
        string rawRule)
    {
        var match = CustomMergeEntityPattern.Match(value);
        if (!match.Success)
        {
            throw new FormatException($"CUSTOM_MERGE_RULES 实体格式错误：{value}；规则：{rawRule}");
        }

        var title = match.Groups["title"].Value.Trim();
        var season = ParsePositiveOptional(match.Groups["season"], "CUSTOM_MERGE_RULES 季数");
        var sources = match.Groups["sources"].Value
            .Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        EnsureKnownSources(definition.Sources, sources, "CUSTOM_MERGE_RULES");
        EnsureDistinct(sources, $"CUSTOM_MERGE_RULES 实体 {value}");
        return new CustomMergeEntity(title, season, sources);
    }

    private static AutoMatchEpisodeSide ParseAutoMatchEpisodeSide(string value, string rawRule)
    {
        var match = AutoMatchEpisodeSidePattern.Match(value);
        if (!match.Success)
        {
            throw new FormatException($"AUTO_MATCH_MAPPING_TABLE 季集格式无效：{rawRule}");
        }

        var title = match.Groups["title"].Value.Trim();
        var season = ParsePositive(match.Groups["season"], "AUTO_MATCH_MAPPING_TABLE 季数");
        var start = ParsePositive(match.Groups["start"], "AUTO_MATCH_MAPPING_TABLE 起始集数");
        int? end = match.Groups["end"].Success
            ? ParsePositive(match.Groups["end"], "AUTO_MATCH_MAPPING_TABLE 结束集数")
            : null;
        if (end is not null && end < start)
        {
            throw new FormatException($"AUTO_MATCH_MAPPING_TABLE 集数范围不能反向：{rawRule}");
        }

        return new AutoMatchEpisodeSide(title, season, start, end);
    }

    private static string FormatAutoMatchEpisodeSide(string title, int season, int start, int? end)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (season <= 0 || start <= 0 || end is <= 0 || end < start)
        {
            throw new FormatException("AUTO_MATCH_MAPPING_TABLE 季数和集数必须是正整数，且范围不能反向");
        }

        var range = end is int endValue ? $"~{endValue:00}" : string.Empty;
        return $"{title.Trim()} S{season:00}E{start:00}{range}";
    }

    private static IReadOnlyList<EpisodeRoute> ParseEpisodeRoutes(string value, string rawRule)
    {
        var routes = new List<EpisodeRoute>();
        foreach (var rawRoute in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = rawRoute.IndexOf('>');
            if (separator <= 0 || separator == rawRoute.Length - 1 || rawRoute.IndexOf('>', separator + 1) >= 0)
            {
                throw new FormatException($"CUSTOM_MERGE_RULES 集数路由格式错误：{rawRoute}；规则：{rawRule}");
            }

            routes.Add(new EpisodeRoute(
                ParseEpisodeRange(rawRoute[..separator].Trim(), rawRule),
                ParseEpisodeRange(rawRoute[(separator + 1)..].Trim(), rawRule)));
        }

        return routes;
    }

    private static EpisodeRange ParseEpisodeRange(string value, string rawRule)
    {
        var match = EpisodeRangePattern.Match(value);
        if (!match.Success)
        {
            throw new FormatException($"CUSTOM_MERGE_RULES 集数范围格式错误：{value}；规则：{rawRule}");
        }

        var start = int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture);
        var end = match.Groups["end"].Success
            ? int.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture)
            : start;
        if (start <= 0 || end < start)
        {
            throw new FormatException($"CUSTOM_MERGE_RULES 集数范围非法：{value}；规则：{rawRule}");
        }

        return new EpisodeRange(start, end);
    }

    private static string FormatCustomMergeEntity(CustomMergeEntity entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.Title);
        if (entity.Season is <= 0)
        {
            throw new FormatException("CUSTOM_MERGE_RULES 季数必须是正整数");
        }

        if (entity.Sources.Count == 0)
        {
            throw new FormatException("CUSTOM_MERGE_RULES 实体至少需要一个来源");
        }

        var season = entity.Season is int value ? $"/S{value:00}" : string.Empty;
        return $"{entity.Title.Trim()}{season}@{string.Join('&', entity.Sources)}";
    }

    private static string FormatEpisodeRoute(EpisodeRoute route) =>
        $"{FormatEpisodeRange(route.Secondary)}>{FormatEpisodeRange(route.Primary)}";

    private static string FormatEpisodeRange(EpisodeRange range)
    {
        if (range.Start <= 0 || range.End < range.Start)
        {
            throw new FormatException("CUSTOM_MERGE_RULES 集数范围非法");
        }

        return range.Start == range.End
            ? $"E{range.Start:00}"
            : $"E{range.Start:00}~E{range.End:00}";
    }

    private static int? ParsePositiveOptional(Group group, string field)
    {
        if (!group.Success)
        {
            return null;
        }

        if (!int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new FormatException($"{field}必须是正整数");
        }

        return value;
    }

    private static int ParsePositive(Group group, string field)
    {
        if (!int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new FormatException($"{field}必须是正整数");
        }

        return value;
    }

    private static void EnsureKnownSources(
        IReadOnlyList<string> allowed,
        IEnumerable<string> values,
        string field)
    {
        foreach (var value in values)
        {
            if (!allowed.Contains(value, StringComparer.Ordinal))
            {
                throw new FormatException($"{field} 包含未声明的来源：{value}");
            }
        }
    }

    private static void EnsureDistinct(IEnumerable<string> values, string field)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
            {
                throw new FormatException($"{field} 不允许重复来源：{value}");
            }
        }
    }

    private static void ValidateRegularExpression(string value)
    {
        var end = value.LastIndexOf('/');
        var pattern = value[1..end];
        var flags = value[(end + 1)..];
        if (flags.Distinct().Count() != flags.Length || flags.Any(flag => flag is not ('i' or 'm' or 's' or 'u')))
        {
            throw new FormatException($"IP_BLACKLIST 正则标记非法：{value}");
        }

        var options = RegexOptions.CultureInvariant;
        if (flags.Contains('i'))
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (flags.Contains('m'))
        {
            options |= RegexOptions.Multiline;
        }

        if (flags.Contains('s'))
        {
            options |= RegexOptions.Singleline;
        }

        try
        {
            _ = new Regex(pattern, options);
        }
        catch (ArgumentException error)
        {
            throw new FormatException($"IP_BLACKLIST 正则非法：{value}", error);
        }
    }

    private sealed record AutoMatchEpisodeSide(
        string Title,
        int Season,
        int StartEpisode,
        int? EndEpisode);
}
