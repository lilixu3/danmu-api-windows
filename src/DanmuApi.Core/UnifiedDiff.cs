namespace DanmuApi.Core;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
    Meta,
}

public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>
/// 解析 GitHub patch 字段（unified diff）。GitHub 在文件过大或二进制时不返回 patch，
/// 调用方必须用 PatchUnavailableReason 显式说明，而不是伪造空 diff。
/// </summary>
public static class UnifiedDiffParser
{
    public static IReadOnlyList<DiffLine> Parse(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var lines = new List<DiffLine>();
        var text = patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        var rawLines = text.Split('\n');
        // GitHub patch 末尾通常带一个换行，Split 会产生一个空尾元素，丢弃它。
        var count = rawLines.Length > 0 && rawLines[^1].Length == 0 ? rawLines.Length - 1 : rawLines.Length;
        for (var index = 0; index < count; index++)
        {
            var line = rawLines[index];
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffLineKind.Hunk, line));
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffLineKind.Meta, line));
                continue;
            }

            if (line.StartsWith('+'))
            {
                lines.Add(new DiffLine(DiffLineKind.Added, line[1..]));
                continue;
            }

            if (line.StartsWith('-'))
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, line[1..]));
                continue;
            }

            lines.Add(new DiffLine(DiffLineKind.Context, line.StartsWith(' ') ? line[1..] : line));
        }

        return lines;
    }
}
