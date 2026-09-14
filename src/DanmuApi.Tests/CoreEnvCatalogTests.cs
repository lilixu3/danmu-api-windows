using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class CoreEnvCatalogTests
{
    [Fact]
    public void ParsesCurrentCoreCatalogCompletely()
    {
        var path = FindRepositoryFile("reference", "core-danmu-api-main", "danmu_api", "configs", "envs.js");

        var definitions = CoreEnvCatalog.ParseFile(path);

        Assert.True(definitions.Count >= 60);
        Assert.Equal(definitions.Count, definitions.Select(definition => definition.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Category));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.False(CoreEnvDefinitionRules.IsHostOwned(definition.Key));
        });

        var outputFormat = Find(definitions, "DANMU_OUTPUT_FORMAT");
        Assert.Equal(CoreEnvType.Select, outputFormat.Type);
        Assert.Contains("json", outputFormat.Options);
        Assert.Contains("artplayer.json", outputFormat.Options);
        Assert.Contains("danuni.binpb", outputFormat.Options);
        Assert.Equal("json", outputFormat.DefaultValue);

        var sourceOrder = Find(definitions, "SOURCE_ORDER");
        Assert.Equal(CoreEnvType.MultiSelect, sourceOrder.Type);
        Assert.Contains("douban", sourceOrder.Options);
        Assert.Equal("douban,360,renren,hanjutv", sourceOrder.DefaultValue);

        var offset = Find(definitions, "DANMU_OFFSET");
        Assert.Contains("bilibili", offset.Sources);
        Assert.Equal(string.Empty, offset.DefaultValue);

        var limit = Find(definitions, "RATE_LIMIT_MAX_REQUESTS");
        Assert.Equal(0m, limit.Minimum);
        Assert.Equal(50m, limit.Maximum);
        Assert.Equal("3", limit.DefaultValue);

        Assert.True(Find(definitions, "TOKEN").IsSensitive);
        Assert.True(Find(definitions, "BILIBILI_COOKIE").IsSensitive);
        Assert.True(Find(definitions, "LOCAL_REDIS_URL").IsSensitive);
        Assert.True(Find(definitions, "ADMIN_TOKEN").IsAdministratorManaged);
        Assert.False(Find(definitions, "BLOCKED_WORDS").IsSensitive);
        Assert.Contains("专业的影视匹配专家", Find(definitions, "AI_MATCH_PROMPT").DefaultValue);
    }

    [Fact]
    public void ParsesDeclaredMetadataAndExplicitEmptyDefault()
    {
        const string source = """
            class Envs {
              static ALLOWED_SOURCES = ['douban', '360'];
              static load() {
                const envVarConfig = {
                  'MODE': { category: 'system', type: 'select', options: ['fast', 'safe'], description: '模式' },
                  'SOURCES': { category: 'source', type: 'multi-select', options: this.ALLOWED_SOURCES, description: '源' },
                  'EMPTY': { category: 'match', type: 'text', description: '允许空值' },
                };
                return {
                  mode: this.get('MODE', 'fast', 'string'),
                  empty: this.get('EMPTY', '', 'string'),
                };
              }
            }
            """;

        var definitions = CoreEnvCatalog.Parse(source);

        // 顺序按核心 load() 的读取顺序：先 MODE 后 EMPTY；
        // SOURCES 在 load() 里没被读到，排在最后（保留声明顺序）。
        Assert.Equal(["MODE", "EMPTY", "SOURCES"], definitions.Select(definition => definition.Key));
        Assert.Equal(["fast", "safe"], Find(definitions, "MODE").Options);
        Assert.Equal(["douban", "360"], Find(definitions, "SOURCES").Options);
        Assert.Equal(string.Empty, Find(definitions, "EMPTY").DefaultValue);
    }

    /// <summary>
    /// 顺序必须来自核心 <c>load()</c> 的读取顺序（= 运行时 originalEnvVars 的键序 = 核心配置页
    /// 每个分类里的显示顺序），而不是 envVarConfig 的声明顺序——实测两者在多数分类上并不一致。
    /// 这里用一个声明顺序与读取顺序刻意相反的例子把它锁住，并覆盖 resolveXxx() 的映射。
    /// </summary>
    [Fact]
    public void OrdersDefinitionsByCoreLoadReadOrder()
    {
        const string source = """
            class Envs {
              static load() {
                const envVarConfig = {
                  'A_FIRST_DECLARED': { category: 'danmu', type: 'text', description: '声明在最前' },
                  'B_SECOND': { category: 'danmu', type: 'text', description: '中间' },
                  'C_THIRD': { category: 'danmu', type: 'text', description: '声明在最后' },
                };
                return {
                  cThird: this.get('C_THIRD', '', 'string'),
                  bSecond: this.resolveBSecond(),
                  aFirst: this.get('A_FIRST_DECLARED', '', 'string'),
                };
              }
              static resolveBSecond() {
                return this.get('B_SECOND', '', 'string').trim();
              }
            }
            """;

        var definitions = CoreEnvCatalog.Parse(source);

        // 读取顺序是 C_THIRD → B_SECOND(resolve) → A_FIRST_DECLARED，与声明顺序完全相反
        Assert.Equal(
            ["C_THIRD", "B_SECOND", "A_FIRST_DECLARED"],
            definitions.Select(definition => definition.Key));
    }

    [Theory]
    [InlineData("const envVarConfig = { 'BAD': { category: 'x', type: 'computed', description: 'x' } };", "不支持的类型")]
    [InlineData("const envVarConfig = { 'BAD': { category: 'x', type: 'text', description: makeText() } };", "不支持的 envVarConfig 表达式")]
    [InlineData("const envVarConfig = { 'BAD': { category: 'x', type: 'select', description: 'x', options: [...unknown] } };", "不支持的数组展开表达式")]
    [InlineData("const envVarConfig = { 'DANMU_API_PORT': { category: 'x', type: 'number', description: 'x' } };", "Desktop 宿主变量")]
    [InlineData("const envVarConfig = { 'BAD': { category: 'x', type: 'text', description: 'x', future: true } };", "不支持的元数据字段")]
    public void RejectsUnsupportedOrUnsafeCatalogSyntax(string source, string expected)
    {
        var error = Assert.Throws<CoreEnvCatalogException>(() => CoreEnvCatalog.Parse(source));

        Assert.Contains(expected, error.Message);
        Assert.True(error.Line > 0);
        Assert.True(error.Column > 0);
    }

    [Fact]
    public void RejectsDuplicateVariables()
    {
        const string source = """
            const envVarConfig = {
              'DUP': { category: 'x', type: 'text', description: 'first' },
              'DUP': { category: 'x', type: 'text', description: 'second' },
            };
            """;

        var error = Assert.Throws<CoreEnvCatalogException>(() => CoreEnvCatalog.Parse(source));

        Assert.Contains("重复字段", error.Message);
    }

    [Fact]
    public void RejectsUnknownDefaultConstantInsteadOfGuessing()
    {
        const string source = """
            const envVarConfig = {
              'VALUE': { category: 'x', type: 'text', description: 'value' },
            };
            this.get('VALUE', dynamicDefault(), 'string');
            """;

        var error = Assert.Throws<CoreEnvCatalogException>(() => CoreEnvCatalog.Parse(source));

        Assert.Contains("无法安全解析默认值表达式", error.Message);
    }

    private static CoreEnvDefinition Find(IEnumerable<CoreEnvDefinition> definitions, string key) =>
        definitions.Single(definition => definition.Key == key);

    private static string FindRepositoryFile(params string[] parts)
    {
        foreach (var origin in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(origin);
            while (directory is not null)
            {
                var candidate = Path.Combine([directory.FullName, .. parts]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"无法定位仓库文件：{Path.Combine(parts)}");
    }
}
