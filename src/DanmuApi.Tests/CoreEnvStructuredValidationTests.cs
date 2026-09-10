using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class CoreEnvStructuredValidationTests
{
    [Fact]
    public void ValidatesStructuredValues()
    {
        CoreEnvStructuredValidation.Validate(Definition("VOD_SERVERS"), "主站@https://example.com,https://backup.example.com");
        CoreEnvStructuredValidation.Validate(Definition("TITLE_MAPPING_TABLE"), "原名->新名;第二个->另一个");
        CoreEnvStructuredValidation.Validate(Definition("AUTO_MATCH_MAPPING_TABLE"), "原名 S01E01~02 -> 新名 S02E03~04 @qiyi");
        CoreEnvStructuredValidation.Validate(Definition("IP_BLACKLIST"), "127.0.0.1,2001:db8::/32,/^10\\./i");
        CoreEnvStructuredValidation.Validate(Definition("COLOR_POOL"), "16777215,0,123456");
    }

    [Fact]
    public void ValidatesSourceAndPlatformOrderUsingDifferentCombinationRules()
    {
        var source = new CoreEnvDefinition(
            "SOURCE_ORDER", "source", CoreEnvType.MultiSelect, "source order",
            ["bilibili", "dandan"], [], null, null, null, false, false);
        var platform = new CoreEnvDefinition(
            "PLATFORM_ORDER", "source", CoreEnvType.MultiSelect, "platform order",
            ["bilibili", "dandan"], [], null, null, null, false, false);

        Assert.Throws<FormatException>(() => CoreEnvStructuredValidation.Validate(source, "bilibili&dandan"));
        CoreEnvStructuredValidation.Validate(platform, "bilibili&dandan");
        Assert.Throws<FormatException>(() => CoreEnvStructuredValidation.Validate(platform, "dandan&bilibili,bilibili&dandan"));
    }

    [Theory]
    [InlineData("VOD_SERVERS", "broken-url")]
    [InlineData("TITLE_MAPPING_TABLE", "missing-arrow")]
    [InlineData("AUTO_MATCH_MAPPING_TABLE", "原名 S01E01~03 -> 新名 S01E01~02 @qiyi")]
    [InlineData("IP_BLACKLIST", "10.0.0.0/99")]
    [InlineData("COLOR_POOL", "not-a-color")]
    public void RejectsMalformedStructuredValues(string key, string value)
    {
        Assert.Throws<FormatException>(() => CoreEnvStructuredValidation.Validate(Definition(key), value));
    }

    private static CoreEnvDefinition Definition(string key) => key switch
    {
        "VOD_SERVERS" => new CoreEnvDefinition(key, "source", CoreEnvType.Text, key, [], [], null, null, null, false, false),
        "TITLE_MAPPING_TABLE" => new CoreEnvDefinition(key, "match", CoreEnvType.Map, key, [], [], null, null, null, false, false),
        "AUTO_MATCH_MAPPING_TABLE" => new CoreEnvDefinition(key, "match", CoreEnvType.Map, key, [], ["qiyi", "youku"], null, null, null, false, false),
        "IP_BLACKLIST" => new CoreEnvDefinition(key, "system", CoreEnvType.Text, key, [], [], null, null, null, false, false),
        "COLOR_POOL" => new CoreEnvDefinition(key, "danmu", CoreEnvType.Text, key, [], [], null, null, null, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };
}
