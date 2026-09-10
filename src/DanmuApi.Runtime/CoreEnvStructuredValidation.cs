using DanmuApi.Core;

namespace DanmuApi.Runtime;

public static class CoreEnvStructuredValidation
{
    public static void Validate(CoreEnvDefinition definition, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        switch (definition.Key)
        {
            case "SOURCE_ORDER":
                ValidateOrderedValues(definition, value, allowComposites: false);
                break;
            case "PLATFORM_ORDER":
                ValidateOrderedValues(definition, value, allowComposites: true);
                break;
            case "MERGE_SOURCE_PAIRS":
                _ = CoreEnvStructuredValues.ParseMergeSourcePairs(definition, value);
                break;
            case "CUSTOM_MERGE_RULES":
                _ = CoreEnvStructuredValues.ParseCustomMergeRules(definition, value);
                break;
            case "VOD_SERVERS":
                ValidateVodServers(value);
                break;
            case "TITLE_MAPPING_TABLE":
                ValidateTitleMappings(value);
                break;
            case "AUTO_MATCH_MAPPING_TABLE":
                _ = CoreEnvStructuredValues.ParseAutoMatchMappings(definition, value);
                break;
            case "DANMU_OFFSET":
                _ = CoreEnvStructuredValues.ParseDanmuOffsets(definition, value);
                break;
            case "IP_BLACKLIST":
                _ = CoreEnvStructuredValues.ParseIpBlacklist(value);
                break;
            case "COLOR_POOL":
                _ = CoreEnvStructuredValues.ParseColorPool(value);
                break;
            case "GRADIENT_COLORS":
                _ = CoreEnvStructuredValues.ParseGradientPalette(value);
                break;
        }
    }

    private static void ValidateOrderedValues(
        CoreEnvDefinition definition,
        string value,
        bool allowComposites)
    {
        var entries = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
        {
            throw new FormatException($"{definition.Key} 必须是逗号分隔的选项列表");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var parts = entry.Split('&', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts.Any(part => !definition.Options.Contains(part, StringComparer.Ordinal)))
            {
                throw new FormatException($"{definition.Key} 包含未声明的平台或来源：{entry}");
            }

            if (!allowComposites && parts.Length > 1)
            {
                throw new FormatException($"{definition.Key} 不允许组合值：{entry}");
            }

            if (parts.Distinct(StringComparer.Ordinal).Count() != parts.Length)
            {
                throw new FormatException($"{definition.Key} 组合值中包含重复选项：{entry}");
            }

            var normalized = string.Join('&', parts.OrderBy(part => part, StringComparer.Ordinal));
            if (!seen.Add(normalized))
            {
                throw new FormatException($"{definition.Key} 不允许重复项：{entry}");
            }
        }
    }

    private static void ValidateVodServers(string value)
    {
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.LastIndexOf('@');
            var url = separator >= 0 ? item[(separator + 1)..].Trim() : item;
            if (url.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                parsed.Scheme is not ("http" or "https"))
            {
                throw new FormatException($"VOD_SERVERS 包含无效 URL：{url}");
            }
        }
    }

    private static void ValidateTitleMappings(string value)
    {
        foreach (var item in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = item.IndexOf("->", StringComparison.Ordinal);
            if (separator <= 0 || separator + 2 >= item.Length || item.IndexOf("->", separator + 2, StringComparison.Ordinal) >= 0)
            {
                throw new FormatException($"TITLE_MAPPING_TABLE 规则格式错误：{item}");
            }
        }
    }
}
