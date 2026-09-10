using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreEnvStructuredValuesTests
{
    private static readonly string[] Sources = ["bilibili", "dandan", "bahamut", "animeko"];

    [Fact]
    public void ParsesAndFormatsMergeSourceGroups()
    {
        var definition = Definition("MERGE_SOURCE_PAIRS", options: Sources);

        var groups = CoreEnvStructuredValues.ParseMergeSourcePairs(
            definition,
            "dandan&animeko&bahamut,bilibili");

        Assert.Equal("dandan", groups[0].Primary);
        Assert.Equal(["animeko", "bahamut"], groups[0].Secondaries);
        Assert.Empty(groups[1].Secondaries);
        Assert.Equal(
            "dandan&animeko&bahamut,bilibili",
            CoreEnvStructuredValues.FormatMergeSourcePairs(groups));
    }

    [Fact]
    public void RejectsDuplicateSourceWithinMergeGroup()
    {
        var definition = Definition("MERGE_SOURCE_PAIRS", options: Sources);

        Assert.Throws<FormatException>(() =>
            CoreEnvStructuredValues.ParseMergeSourcePairs(definition, "dandan&dandan"));
    }

    [Fact]
    public void ParsesAndFormatsCustomMergeRules()
    {
        var definition = Definition("CUSTOM_MERGE_RULES", sources: Sources);

        var rules = CoreEnvStructuredValues.ParseCustomMergeRules(
            definition,
            "副标题/S01@bahamut -> 主标题/S03@dandan | E01>E01,E25~E35>E25~E35;副标题@bilibili × 主标题@animeko");

        Assert.False(rules[0].IsBlocked);
        Assert.Equal(1, rules[0].Secondary.Season);
        Assert.Equal(new EpisodeRange(25, 35), rules[0].Routes[1].Secondary);
        Assert.True(rules[1].IsBlocked);
        Assert.Equal(
            "副标题/S01@bahamut -> 主标题/S03@dandan | E01>E01,E25~E35>E25~E35;副标题@bilibili × 主标题@animeko",
            CoreEnvStructuredValues.FormatCustomMergeRules(rules));
    }

    [Theory]
    [InlineData("副标题@unknown -> 主标题@dandan")]
    [InlineData("副标题@bilibili × 主标题@dandan | E01>E01")]
    [InlineData("副标题@bilibili -> 主标题@dandan | E02~E01>E01")]
    [InlineData("副标题@bilibili -> 主标题@dandan | broken")]
    public void RejectsInvalidCustomMergeRules(string value)
    {
        var definition = Definition("CUSTOM_MERGE_RULES", sources: Sources);

        Assert.Throws<FormatException>(() =>
            CoreEnvStructuredValues.ParseCustomMergeRules(definition, value));
    }

    [Fact]
    public void ParsesAndFormatsAutoMatchMappings()
    {
        var definition = Definition("AUTO_MATCH_MAPPING_TABLE", sources: ["qiyi", "youku"]);

        var rules = CoreEnvStructuredValues.ParseAutoMatchMappings(
            definition,
            "永生 S05E02 -> 永生 S01E58;海贼王 S02E01~03 -> 航海王(1999)【动漫】 S01E62~64 @qiyi");

        Assert.Equal(2, rules.Count);
        Assert.Null(rules[0].SourceEndEpisode);
        Assert.Equal("航海王(1999)【动漫】", rules[1].TargetDisplayTitle);
        Assert.Equal(3, rules[1].SourceEndEpisode);
        Assert.Equal(64, rules[1].TargetEndEpisode);
        Assert.Equal("qiyi", rules[1].TargetPlatform);
        Assert.Equal(
            "永生 S05E02 -> 永生 S01E58;海贼王 S02E01~03 -> 航海王(1999)【动漫】 S01E62~64 @qiyi",
            CoreEnvStructuredValues.FormatAutoMatchMappings(definition, rules));
    }

    [Theory]
    [InlineData("永生 S05E02~03 -> 永生 S01E58")]
    [InlineData("永生 S05E02~04 -> 永生 S01E58~59")]
    [InlineData("永生 S00E02 -> 永生 S01E58")]
    [InlineData("永生 S05E02 -> 永生 S01E58 @unknown")]
    [InlineData("永生 S05E03~02 -> 永生 S01E58~59")]
    [InlineData("永生 S05E02 -> (1999)【动漫】 S01E58")]
    public void RejectsInvalidAutoMatchMappings(string value)
    {
        var definition = Definition("AUTO_MATCH_MAPPING_TABLE", sources: ["qiyi", "youku"]);

        Assert.Throws<FormatException>(() =>
            CoreEnvStructuredValues.ParseAutoMatchMappings(definition, value));
    }

    [Fact]
    public void ParsesAndFormatsDanmuOffsets()
    {
        var definition = Definition("DANMU_OFFSET", sources: Sources);

        var rules = CoreEnvStructuredValues.ParseDanmuOffsets(
            definition,
            "overlord/S01:90,re-zero/S02/E03@dandan&bilibili:10,东方/S03/E02@all%:11.5");

        Assert.Equal(3, rules.Count);
        Assert.Equal(["dandan", "bilibili"], rules[1].Sources);
        Assert.True(rules[2].AllSources);
        Assert.True(rules[2].UsePercent);
        Assert.Equal(
            "overlord/S01:90,re-zero/S02/E03@dandan&bilibili:10,东方/S03/E02@all%:11.5",
            CoreEnvStructuredValues.FormatDanmuOffsets(rules));
    }

    [Theory]
    [InlineData("番剧/E02:10")]
    [InlineData("番剧/S01@unknown:10")]
    [InlineData("番剧/S00:10")]
    [InlineData("番剧/S01:NaN")]
    public void RejectsInvalidDanmuOffsets(string value)
    {
        var definition = Definition("DANMU_OFFSET", sources: Sources);

        Assert.Throws<FormatException>(() =>
            CoreEnvStructuredValues.ParseDanmuOffsets(definition, value));
    }

    [Fact]
    public void ConvertsColorPoolBetweenCoreDecimalAndLocalHex()
    {
        var colors = CoreEnvStructuredValues.ParseColorPool("16711680,65280,255");

        Assert.Equal([0xFF0000u, 0x00FF00u, 0x0000FFu], colors);
        Assert.Equal("#FF0000", CoreEnvStructuredValues.FormatHexColor(colors[0]));
        Assert.Equal(0xFF6600u, CoreEnvStructuredValues.ParseHexColor("#FF6600"));
        Assert.Equal("16711680,65280,255", CoreEnvStructuredValues.FormatColorPool(colors));
    }

    [Fact]
    public void ParsesGradientSkinOrAtLeastTwoCustomColors()
    {
        var skin = CoreEnvStructuredValues.ParseGradientPalette("sunset");
        var custom = CoreEnvStructuredValues.ParseGradientPalette("16711680,255");

        Assert.Equal("sunset", skin.Skin);
        Assert.Empty(skin.Colors);
        Assert.Null(custom.Skin);
        Assert.Equal(2, custom.Colors.Count);
        Assert.Equal("sunset", CoreEnvStructuredValues.FormatGradientPalette(skin));
        Assert.Equal("16711680,255", CoreEnvStructuredValues.FormatGradientPalette(custom));
        Assert.Throws<FormatException>(() => CoreEnvStructuredValues.ParseGradientPalette("16711680"));
    }

    [Fact]
    public void ClassifiesAndFormatsIpBlacklistEntries()
    {
        var entries = CoreEnvStructuredValues.ParseIpBlacklist(
            "127.0.0.1,2001:db8::/32,/^10\\./i");

        Assert.Equal(IpBlacklistEntryType.Address, entries[0].Type);
        Assert.Equal(IpBlacklistEntryType.Cidr, entries[1].Type);
        Assert.Equal(IpBlacklistEntryType.RegularExpression, entries[2].Type);
        Assert.Equal(
            "127.0.0.1,2001:db8::/32,/^10\\./i",
            CoreEnvStructuredValues.FormatIpBlacklist(entries));
    }

    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("/^10\\./ii")]
    [InlineData("not-an-address")]
    public void RejectsInvalidIpBlacklistEntries(string value)
    {
        Assert.Throws<FormatException>(() =>
            CoreEnvStructuredValues.ParseIpBlacklist(value));
    }

    private static CoreEnvDefinition Definition(
        string key,
        IReadOnlyList<string>? options = null,
        IReadOnlyList<string>? sources = null) =>
        new(
            key,
            "test",
            CoreEnvType.Text,
            key,
            options ?? [],
            sources ?? [],
            null,
            null,
            null,
            false,
            false);
}
