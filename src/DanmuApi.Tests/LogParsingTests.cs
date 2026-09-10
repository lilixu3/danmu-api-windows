using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed class LogParsingTests
{
    [Fact]
    public void CoreLogLineParsesTimestampLevelAndCategory()
    {
        var parsed = LogLineParser.Parse(
            "[2026-09-01T12:34:56.789+08:00] info: [bilibili] 搜索命中 12 条",
            LogSourceKind.CoreOutput);

        Assert.False(parsed.IsContinuation);
        Assert.Equal(LogLevel.Info, parsed.Level);
        Assert.Equal("2026-09-01T12:34:56.789+08:00", parsed.Timestamp);
        Assert.Equal("bilibili", parsed.Category);
        Assert.Equal("[bilibili] 搜索命中 12 条", parsed.Message);
    }

    [Theory]
    [InlineData("error", LogLevel.Error)]
    [InlineData("warn", LogLevel.Warn)]
    [InlineData("warning", LogLevel.Warn)]
    [InlineData("info", LogLevel.Info)]
    [InlineData("success", LogLevel.Success)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("trace", LogLevel.Debug)]
    public void LevelTokensMapToExpectedLevels(string token, LogLevel expected) =>
        Assert.Equal(expected, LogLineParser.ParseLevel(token));

    [Fact]
    public void UnknownLevelFallsBackToOtherButStderrIsError()
    {
        var stdout = LogLineParser.Parse("[2026-09-01T00:00:00Z] something: raw line", LogSourceKind.CoreOutput);
        var stderr = LogLineParser.Parse("[2026-09-01T00:00:00Z] something: raw line", LogSourceKind.CoreError);

        Assert.Equal(LogLevel.Other, stdout.Level);
        Assert.Equal(LogLevel.Error, stderr.Level);
    }

    [Fact]
    public void ContinuationLineHasNoLevelOrCategory()
    {
        var parsed = LogLineParser.Parse("    at someFunction (worker.js:42:7)", LogSourceKind.CoreOutput);

        Assert.True(parsed.IsContinuation);
        Assert.Null(parsed.Level);
        Assert.Null(parsed.Category);
    }

    [Fact]
    public void TimestampLikeTagsAreNotTreatedAsCategory()
    {
        var parsed = LogLineParser.Parse(
            "[2026-09-01T12:00:00Z] info: [12:00:01] [system] 心跳正常",
            LogSourceKind.CoreOutput);

        Assert.Equal("system", parsed.Category);
    }

    [Fact]
    public void MergeTagsCollapseIntoMergeCategory()
    {
        var merge = LogLineParser.Parse("[2026-09-01T12:00:00Z] info: [匹配] 命中 3 条", LogSourceKind.CoreOutput);
        var variant = LogLineParser.Parse("[2026-09-01T12:00:00Z] info: [VOD Fastest Mode] 搜索完成", LogSourceKind.CoreOutput);

        Assert.Equal("merge", merge.Category);
        Assert.Equal("vod", variant.Category);
    }

    [Fact]
    public void FilterMatchesMessageCategoryAndLevelTogether()
    {
        var entries = new[]
        {
            Entry(1, LogLevel.Info, "[bilibili] 搜索命中", "bilibili"),
            Entry(2, LogLevel.Error, "[tencent] 请求失败", "tencent"),
            Entry(3, LogLevel.Info, "[bilibili] 搜索失败但未报错", "bilibili"),
        };

        var filtered = LogFilter.Apply(
            entries,
            new LogQuery("命中", new HashSet<LogLevel> { LogLevel.Info }, "bilibili"));

        Assert.Single(filtered);
        Assert.Equal(1, filtered[0].Sequence);
    }

    [Fact]
    public void SearchIsCaseInsensitiveByDefaultAndHonoursCaseSensitiveFlag()
    {
        var entries = new[] { Entry(1, LogLevel.Info, "Token 已刷新", "system") };

        Assert.Single(LogFilter.Apply(entries, new LogQuery("token")));
        Assert.Empty(LogFilter.Apply(entries, new LogQuery("token", CaseSensitive: true)));
    }

    [Fact]
    public void PlainTextUsesFilteredEntriesOnly()
    {
        var entries = new[]
        {
            Entry(1, LogLevel.Info, "first", "bilibili"),
            Entry(2, LogLevel.Error, "second", "tencent"),
        };
        var filtered = LogFilter.Apply(entries, new LogQuery("first"));

        var text = LogFilter.ToPlainText(filtered);

        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.DoesNotContain("second", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CategoryAndLevelCountsAreDerivedFromAllEntries()
    {
        var entries = new[]
        {
            Entry(1, LogLevel.Info, "[bilibili] a", "bilibili"),
            Entry(2, LogLevel.Error, "[bilibili] b", "bilibili"),
            Entry(3, LogLevel.Warn, "[tencent] c", "tencent"),
        };

        var categories = LogFilter.CountCategories(entries);
        var levels = LogFilter.CountLevels(entries);

        Assert.Equal("bilibili", categories[0].Name);
        Assert.Equal(2, categories[0].Count);
        Assert.Equal(1, levels[LogLevel.Error]);
        Assert.Equal(1, levels[LogLevel.Warn]);
        Assert.Equal(1, levels[LogLevel.Info]);
    }

    [Fact]
    public async Task MissingLogFileReportsDiagnosticInsteadOfThrowing()
    {
        using var directory = new TemporaryDirectory();
        var reader = new LogFileTailReader();

        var result = await reader.ReadAsync(Path.Combine(directory.Path, "missing.log"), 0, null);

        Assert.False(result.FileExists);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("不存在", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialLineIsHeldUntilItEndsWithNewline()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "node-stdout.log");
        await File.WriteAllTextAsync(path, "[t] info: first line\n[t] info: partial");
        var reader = new LogFileTailReader();

        var first = await reader.ReadAsync(path, 0, null);
        var second = await reader.ReadAsync(path, first.Position, first.PendingRemainder);

        Assert.Equal(["[t] info: first line"], first.Lines);
        Assert.Equal("[t] info: partial", first.PendingRemainder);
        Assert.Empty(second.Lines);
        Assert.Equal("[t] info: partial", second.PendingRemainder);
        Assert.Equal(first.Position, second.Position);
    }

    [Fact]
    public async Task PartialLineCompletesWithoutDuplicatingContent()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "node-stdout.log");
        await File.WriteAllTextAsync(path, "[t] info: partial");
        var reader = new LogFileTailReader();

        var first = await reader.ReadAsync(path, 0, null);
        await File.AppendAllTextAsync(path, " completed\n[t] info: next\n");
        var second = await reader.ReadAsync(path, first.Position, first.PendingRemainder);

        Assert.Equal(["[t] info: partial completed", "[t] info: next"], second.Lines);
        Assert.Null(second.PendingRemainder);
    }

    [Fact]
    public async Task TruncatedFileRestartsFromBeginning()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "node-stdout.log");
        await File.WriteAllTextAsync(path, "line one\nline two\n");
        var reader = new LogFileTailReader();

        var first = await reader.ReadAsync(path, 0, null);
        await File.WriteAllTextAsync(path, "fresh line\n");
        var second = await reader.ReadAsync(path, first.Position, first.PendingRemainder);

        Assert.True(second.Restarted);
        Assert.Equal(["fresh line"], second.Lines);
    }

    private static LogEntry Entry(long sequence, LogLevel level, string message, string category) =>
        new(sequence, LogSourceKind.CoreOutput, "2026-09-01T00:00:00Z", level, message, category, message);
}
