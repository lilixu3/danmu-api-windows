using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

/// <summary>
/// `.env` 值编解码与移动端 <c>DotEnvCodec</c>（`danmu-api-android`）保持同一套契约：
/// - 读：剥一层匹配引号；双引号内只还原 <c>\\ \" \n \r \t</c>，其余反斜杠序列原样保留，且**永不抛异常**
///   （0.4.4 的故障就是遇到 <c>\d</c> 直接抛 FormatException，把服务启动和应用启动一起打挂）；
/// - 写：只在必要时加引号；反斜杠**只在后面跟着会被读成转义的字符时才**写成 <c>\\</c>，
///   所以正则与 Windows 路径写进文件后与用户输入逐字相同，反复保存也不会每次多加一层。
/// 宿主 <c>android-server.js</c> 的 unescapeDoubleQuotedEnvValue 也按同一套规则还原。
/// </summary>
public sealed class DotEnvValueCodecTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    [Fact]
    public void CoreWrittenBlockedWordsRegexIsReadVerbatim()
    {
        // 复现事故现场：核心 node-handler 对 BLOCKED_WORDS 加双引号但不转义反斜杠。
        var values = ReadRaw("""
            BLOCKED_WORDS="/^\d+广告/"
            """);

        Assert.Equal(@"/^\d+广告/", values["BLOCKED_WORDS"]);
    }

    [Fact]
    public void UnknownEscapesAreKeptVerbatimAndNeverThrow()
    {
        var values = ReadRaw("""
            WITH_D="/^\d+$/"
            WITH_W="\w+\S*"
            WITH_DOT="a\.b"
            WITH_A="\a"
            TRAILING="tail\\"
            """);

        Assert.Equal(@"/^\d+$/", values["WITH_D"]);
        Assert.Equal(@"\w+\S*", values["WITH_W"]);
        Assert.Equal(@"a\.b", values["WITH_DOT"]);
        Assert.Equal(@"\a", values["WITH_A"]);
        Assert.Equal(@"tail\", values["TRAILING"]);
    }

    [Fact]
    public void WindowsPathInDoubleQuotesIsReadVerbatim()
    {
        var values = ReadRaw("""
            COOKIE_PATH="C:\Users\admin\弹幕"
            """);

        Assert.Equal(@"C:\Users\admin\弹幕", values["COOKIE_PATH"]);
    }

    [Fact]
    public void EscapedBackslashDecodesToOneLikeTheDesktopHost()
    {
        var values = ReadRaw("""
            PATTERN="/^\\d+$/"
            """);

        Assert.Equal(@"/^\d+$/", values["PATTERN"]);
    }

    [Fact]
    public void BareValueWithBackslashesIsReadVerbatim()
    {
        // 核心自带 .env.example 里 TITLE_NOISE_FILTER 就是这个形态（裸值 + \\，宿主不还原裸值）。
        var values = ReadRaw("""
            TITLE_NOISE_FILTER=[（(\\[](?:臻彩)[\\])）]
            """);

        Assert.Equal(@"[（(\\[](?:臻彩)[\\])）]", values["TITLE_NOISE_FILTER"]);
    }

    [Fact]
    public void SingleQuotedValueOnlyStripsTheQuotes()
    {
        var values = ReadRaw("""
            BLOCKED_WORDS='/^\d+/'
            """);

        Assert.Equal(@"/^\d+/", values["BLOCKED_WORDS"]);
    }

    [Fact]
    public void KnownEscapesAreDecodedExactlyLikeTheHost()
    {
        var values = ReadRaw("""
            MULTI="a\nb"
            CR="a\rb"
            TAB="a\tb"
            BACKSLASH="a\\b"
            QUOTE="he said \"hi\""
            """);

        Assert.Equal("a\nb", values["MULTI"]);
        Assert.Equal("a\rb", values["CR"]);
        Assert.Equal("a\tb", values["TAB"]);
        Assert.Equal(@"a\b", values["BACKSLASH"]);
        Assert.Equal("he said \"hi\"", values["QUOTE"]);
    }

    /// <summary>
    /// 回归（用户实测反馈）：在配置页输入正则后，文件里的反斜杠必须和输入的一样，
    /// 而且**反复保存不能每次多加一层**（移动端 DotEnvCodec 有同名回归测试）。
    /// </summary>
    [Fact]
    public void TypedRegexIsWrittenVerbatimAndSavingTwiceIsIdempotent()
    {
        const string original = @"/\d+[^\w\d\s]+/,/^.$/,/.{20,}/,/^\d{1,2}[/.]\d{1,2}$/";
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["BLOCKED_WORDS"] = original });
        var firstText = File.ReadAllText(path, Utf8NoBom);
        var firstRead = DotEnvFile.ReadValue(path, "BLOCKED_WORDS");

        // 该值不含空白/=/#/"，按与移动端一致的规则裸写；关键是反斜杠与输入逐字相同、没有一个被翻倍。
        Assert.Contains($"BLOCKED_WORDS={original}", firstText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\\d", firstText, StringComparison.Ordinal);
        Assert.Equal(original, firstRead);

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["BLOCKED_WORDS"] = firstRead! });
        var secondText = File.ReadAllText(path, Utf8NoBom);

        Assert.Equal(firstText, secondText);
        Assert.Equal(original, DotEnvFile.ReadValue(path, "BLOCKED_WORDS"));
    }

    /// <summary>旧版本（0.4.5/0.4.6）写坏的双反斜杠值，再保存一次就会被规范回单反斜杠。</summary>
    [Fact]
    public void LegacyDoubledBackslashesNormalizeOnOneSave()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");
        File.WriteAllText(
            path,
            "BLOCKED_WORDS=\"/\\\\d+[^\\\\w\\\\d\\\\s]+/,/[@#&$%^*+\\\\|/\\\\-_=<>]/\"\n",
            Utf8NoBom);

        var parsed = DotEnvFile.ReadValue(path, "BLOCKED_WORDS");
        Assert.Equal(@"/\d+[^\w\d\s]+/,/[@#&$%^*+\|/\-_=<>]/", parsed);

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["BLOCKED_WORDS"] = parsed });
        var text = File.ReadAllText(path, Utf8NoBom);

        Assert.Contains("BLOCKED_WORDS=\"/\\d+[^\\w\\d\\s]+/,/[@#&$%^*+\\|/\\-_=<>]/\"", text, StringComparison.Ordinal);
        Assert.Equal(parsed, DotEnvFile.ReadValue(path, "BLOCKED_WORDS"));
    }

    /// <summary>含 # 的值必须加引号，但正则反斜杠仍然保持原样（移动端同名用例）。</summary>
    [Fact]
    public void HashValueIsQuotedWhileRegexBackslashesStayStable()
    {
        const string value = @"/[@#&$%^*+\|/\-_=<>]/";
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["BLOCKED_WORDS"] = value });
        var text = File.ReadAllText(path, Utf8NoBom);

        Assert.Contains($"BLOCKED_WORDS=\"{value}\"", text, StringComparison.Ordinal);
        Assert.Equal(value, DotEnvFile.ReadValue(path, "BLOCKED_WORDS"));
    }

    /// <summary>值里本来就有 <c>\n</c>/<c>\t</c>/<c>\\</c>/<c>\"</c> 这类字面量时，必须转义一层才能原样往返。</summary>
    [Fact]
    public void BackslashesThatLookLikeEscapesRoundTrip()
    {
        // 值的字面内容：prefix # literal\n literal\t literal\r slash\\ quote\" done
        var original = "prefix # literal\\n literal\\t literal\\r slash\\\\ quote\\\" done";
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["VALUE"] = original });
        var firstText = File.ReadAllText(path, Utf8NoBom);
        var firstRead = DotEnvFile.ReadValue(path, "VALUE");

        Assert.Equal(original, firstRead);

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["VALUE"] = firstRead! });

        Assert.Equal(firstText, File.ReadAllText(path, Utf8NoBom));
        Assert.Equal(original, DotEnvFile.ReadValue(path, "VALUE"));
    }

    /// <summary>反斜杠后面跟真实的换行/制表符时也要能原样往返（移动端同名用例）。</summary>
    [Fact]
    public void BackslashBeforeRealControlCharacterRoundTrips()
    {
        var original = "prefix # slash-before-newline\\\nslash-before-tab\\\tdone";
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["VALUE"] = original });

        Assert.Equal(original, DotEnvFile.ReadValue(path, "VALUE"));
        Assert.Single(File.ReadAllLines(path), line => line.StartsWith("VALUE=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"/^\d+$/")]
    [InlineData(@"C:\Users\admin\弹幕")]
    [InlineData(@"a\b\\c")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("he said \"hi\"")]
    [InlineData("with # hash")]
    [InlineData("中文 与 spaces")]
    [InlineData("a,b;c|d")]
    public void WorkbenchEditableValuesRoundTrip(string value)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["SPECIAL"] = value });

        Assert.Equal(value, DotEnvFile.ReadValue(path, "SPECIAL"));
    }

    [Theory]
    [InlineData(@"'$1'")]
    [InlineData(@"C:\Users\admin\弹幕")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("he said \"hi\"")]
    [InlineData("with # hash")]
    [InlineData("中文 与 spaces")]
    [InlineData(@"/^\d+$/")]
    [InlineData("a\\nb")]
    public void WrittenValuesSurviveTheCoreReader(string value)
    {
        // 回读校验过去只用宿主自己的解析器，所以「宿主写的转义核心认不认」这件事测试根本看不见。
        // 这里按桌面核心的真实读取链路复核一遍：android-server.js 的 parseDotEnv +
        // unescapeDoubleQuotedEnvValue（桌面下 14 个 RAW 键也走这条路，见 specs/01 §5）。
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["SPECIAL"] = value });

        var line = Assert.Single(File.ReadAllLines(path),
            candidate => candidate.StartsWith("SPECIAL=", StringComparison.Ordinal));
        Assert.Equal(value, ReadTheWayCoreDoes(line["SPECIAL=".Length..]));
    }

    /// <summary>android-server.js:1132-1170 的等价实现：trim → 成对引号 → 五个转义，其余反斜杠原样。</summary>
    private static string ReadTheWayCoreDoes(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            var builder = new StringBuilder(inner.Length);
            for (var index = 0; index < inner.Length; index++)
            {
                var character = inner[index];
                if (character != '\\' || index == inner.Length - 1)
                {
                    builder.Append(character);
                    continue;
                }

                var next = inner[++index];
                builder.Append(next switch
                {
                    '\\' => "\\",
                    '"' => "\"",
                    'n' => "\n",
                    'r' => "\r",
                    't' => "\t",
                    _ => "\\" + next,
                });
            }

            return builder.ToString();
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            return value[1..^1];
        }

        return value;
    }

    [Fact]
    public void MultiLineValueStaysOnOnePhysicalLine()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");

        DotEnvFile.UpdateValues(path, new Dictionary<string, string?> { ["SPECIAL"] = "a\nb" });

        Assert.Single(File.ReadAllLines(path), line => line.StartsWith("SPECIAL=", StringComparison.Ordinal));
        Assert.Equal("a\nb", DotEnvFile.ReadValue(path, "SPECIAL"));
    }

    private static IReadOnlyDictionary<string, string> ReadRaw(string content)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, ".env");
        File.WriteAllText(path, content, Utf8NoBom);
        return DotEnvFile.ReadValues(path);
    }
}
