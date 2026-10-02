namespace DanmuApi.Core;

/// <summary>
/// 读取"正被另一个句柄写"的日志文件尾部。
///
/// 必须显式用 <c>FileShare.ReadWrite</c> 打开：Windows 的共享检查是<b>双向</b>的——读方声明的
/// 共享模式也要允许"已经存在的写句柄"继续写。日志泵持有 <c>FileAccess.Write</c> 的句柄时，
/// 用 <c>File.ReadLines</c>（它带 <c>FileShare.Read</c>=不允许别人写）读会直接抛
/// "文件正由另一进程使用"（实测：同一个文件，<c>FileShare.ReadWrite</c> 读成功、<c>FileShare.Read</c> 失败）。
/// 进程活着时要读它自己的日志（启动超时、状态异常）正属于这种情况，读不到就等于把诊断信息丢了。
/// </summary>
public static class LogTail
{
    public const string MissingFileText = "（无日志）";

    public static string Read(string? path, int maxLines = 40)
    {
        if (maxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines), maxLines, "读取行数必须大于零");
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return MissingFileText;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var buffer = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                if (buffer.Count == maxLines)
                {
                    buffer.Dequeue();
                }

                buffer.Enqueue(line);
            }

            return buffer.Count == 0 ? MissingFileText : string.Join(Environment.NewLine, buffer);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"（读取日志失败: {error.Message}）";
        }
    }
}
