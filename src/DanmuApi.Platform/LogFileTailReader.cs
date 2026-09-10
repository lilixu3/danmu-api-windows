using System.Text;
using DanmuApi.Core;

namespace DanmuApi.Platform;

public sealed record LogFileReadResult(
    bool FileExists,
    long Position,
    string? PendingRemainder,
    IReadOnlyList<string> Lines,
    bool Restarted,
    string? Diagnostic)
{
    public static LogFileReadResult Missing(string diagnostic) =>
        new(false, 0, null, [], false, diagnostic);
}

/// <summary>
/// 增量读取日志文件。核心进程正在写入，因此使用共享读写的只读流；
/// 只消费以换行结束的完整行，半行留到下一次读取，避免把未写完的日志拆成两行。
/// 文件被清空或轮转（长度回退）时从头重新读取，而不是静默跳过。
/// </summary>
public sealed class LogFileTailReader
{
    public async Task<LogFileReadResult> ReadAsync(
        string path,
        long position,
        string? pendingRemainder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        FileInfo info;
        try
        {
            info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return LogFileReadResult.Missing($"日志文件不存在：{fullPath}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return LogFileReadResult.Missing($"读取日志文件信息失败：{error.Message}");
        }

        var restarted = false;
        if (position > info.Length)
        {
            position = 0;
            pendingRemainder = null;
            restarted = true;
        }

        if (position == info.Length && pendingRemainder is null)
        {
            return new LogFileReadResult(true, position, null, [], false, null);
        }

        var buffer = new List<byte>(checked((int)Math.Min(info.Length - position, 8 * 1024 * 1024)));
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Seek(position, SeekOrigin.Begin);
            var chunk = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    buffer.Add(chunk[index]);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new LogFileReadResult(true, position, pendingRemainder, [], restarted, $"读取日志失败：{error.Message}");
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var lines = new List<string>();
        var pending = pendingRemainder;
        var start = 0;
        string? remainder = null;
        while (true)
        {
            var separator = text.IndexOf('\n', start);
            if (separator < 0)
            {
                var tail = start < text.Length ? text[start..] : string.Empty;
                remainder = pending is null ? tail : pending + tail;
                if (remainder.Length == 0)
                {
                    remainder = null;
                }

                break;
            }

            var end = separator;
            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            var segment = text[start..end];
            lines.Add(pending is null ? segment : pending + segment);
            pending = null;
            start = separator + 1;
        }

        // 位置按实际读取的字节推进：未换行的半行留在 remainder 里，
        // 若只推进到换行处，下次读取会把同一段半行再读一遍并拼出重复内容。
        var newPosition = position + buffer.Count;
        return new LogFileReadResult(true, newPosition, remainder, lines, restarted, null);
    }
}
