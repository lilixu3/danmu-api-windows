using System.Globalization;
using System.Text;

namespace DanmuApi.Core;

public sealed record LogQuery(
    string? SearchText = null,
    IReadOnlySet<LogLevel>? Levels = null,
    string? Category = null,
    bool CaseSensitive = false)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(SearchText) &&
                           (Levels is null || Levels.Count == 0) &&
                           string.IsNullOrWhiteSpace(Category);

    public bool Matches(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Levels is { Count: > 0 } && !Levels.Contains(entry.Level))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Category) &&
            !string.Equals(entry.Category, Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var needle = SearchText?.Trim();
        if (string.IsNullOrEmpty(needle))
        {
            return true;
        }

        return Contains(entry.Message, needle, CaseSensitive) ||
               Contains(entry.Raw, needle, CaseSensitive);
    }

    public int FirstIndexOf(string value)
    {
        var needle = SearchText?.Trim();
        return string.IsNullOrEmpty(needle)
            ? -1
            : value.IndexOf(needle, CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static bool Contains(string value, string needle, bool caseSensitive) =>
        value.Contains(needle, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

public static class LogFilter
{
    public static IReadOnlyList<LogEntry> Apply(
        IEnumerable<LogEntry> entries,
        LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(query);
        return entries.Where(query.Matches).ToArray();
    }

    /// <summary>
    /// 导出/复制时使用，保持与筛选结果一致：默认导出原始行（保真），
    /// 可选包含来源与序号的表格化文本。
    /// </summary>
    public static string ToPlainText(IEnumerable<LogEntry> entries, bool includeSource = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            if (includeSource)
            {
                builder.Append('[')
                    .Append(FormatSource(entry.Source))
                    .Append("] ");
            }

            builder.Append(entry.Raw);
            if (!entry.Raw.EndsWith("\n", StringComparison.Ordinal))
            {
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }

    public static string FormatSource(LogSourceKind source) => source switch
    {
        LogSourceKind.CoreApi => "核心日志",
        LogSourceKind.CoreOutput => "Node 输出",
        LogSourceKind.CoreError => "Node 错误",
        LogSourceKind.Host => "宿主日志",
        _ => source.ToString(),
    };

    public static IReadOnlyList<LogCategoryCount> CountCategories(IEnumerable<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Category))
            .GroupBy(entry => entry.Category!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new LogCategoryCount(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyDictionary<LogLevel, int> CountLevels(IEnumerable<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var counts = new Dictionary<LogLevel, int>();
        foreach (var level in Enum.GetValues<LogLevel>())
        {
            counts[level] = 0;
        }

        foreach (var entry in entries)
        {
            counts[entry.Level] = counts.TryGetValue(entry.Level, out var value) ? value + 1 : 1;
        }

        return counts;
    }
}

public sealed record LogCategoryCount(string Name, int Count)
{
    public string DisplayText => string.Format(CultureInfo.InvariantCulture, "{0} ({1})", Name, Count);
}
