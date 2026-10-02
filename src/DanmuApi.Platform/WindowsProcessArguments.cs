using System.Text;
using DanmuApi.Runtime;

namespace DanmuApi.Platform;

/// <summary>
/// Windows CRT 参数拆分与宿主启动形状验证。只接受宿主实际生成的调用形状；无法证明归属时拒绝，
/// 不把任意参数/子串里的路径当成入口或配置，也不在错误里回显命令行。
/// </summary>
public static class WindowsProcessArguments
{
    public static bool MatchesNodeEntryPoint(string? commandLine, string expectedScript) =>
        TryParse(commandLine, out var arguments)
        && arguments.Count == 2
        && PathsEqual(arguments[1], expectedScript);

    public static bool MatchesFrpConfig(string? commandLine, string expectedConfig)
    {
        if (!TryParse(commandLine, out var arguments)) return false;
        // 不认领 tcp/verify 等手工子命令、重复配置参数或其他无法证明与宿主一致的选项。
        if (arguments.Count == 3 && arguments[1] is "-c" or "--config")
            return PathsEqual(arguments[2], expectedConfig);
        if (arguments.Count == 2)
        {
            foreach (var prefix in new[] { "--config=", "-c=" })
                if (arguments[1].StartsWith(prefix, StringComparison.Ordinal))
                    return PathsEqual(arguments[1][prefix.Length..], expectedConfig);
        }

        return false;
    }

    public static bool TryParse(string? commandLine, out IReadOnlyList<string> arguments)
    {
        arguments = [];
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Contains('\0')) return false;
        var parsed = new List<string>();
        var index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && IsSeparator(commandLine[index])) index++;
            if (index == commandLine.Length) break;
            var value = new StringBuilder();
            var quoted = false;
            while (index < commandLine.Length && (quoted || !IsSeparator(commandLine[index])))
            {
                var slashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    slashes++;
                    index++;
                }

                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if ((slashes & 1) != 0)
                    {
                        value.Append('"');
                        index++;
                    }
                    else if (quoted && index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                    {
                        value.Append('"');
                        index += 2;
                    }
                    else
                    {
                        quoted = !quoted;
                        index++;
                    }
                }
                else
                {
                    value.Append('\\', slashes);
                    if (index < commandLine.Length && (quoted || !IsSeparator(commandLine[index])))
                        value.Append(commandLine[index++]);
                }
            }

            // Windows 可容忍未闭合引号；归属验证选择拒绝这种无法证明由宿主生成的形状。
            if (quoted) return false;
            parsed.Add(value.ToString());
        }

        if (parsed.Count == 0 || parsed[0].Length == 0) return false;
        arguments = parsed;
        return true;
    }

    private static bool IsSeparator(char character) => character is ' ' or '\t';

    private static bool PathsEqual(string actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected)
            || !Path.IsPathFullyQualified(actual) || !Path.IsPathFullyQualified(expected)) return false;
        try
        {
            return string.Equals(RuntimeValidation.CanonicalPath(actual), RuntimeValidation.CanonicalPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            // 非法路径是显式归属拒绝，不尝试猜测相对路径的 cwd。
            return false;
        }
    }
}
