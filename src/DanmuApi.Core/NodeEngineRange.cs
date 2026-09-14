using System.Globalization;

namespace DanmuApi.Core;

/// <summary>
/// Strict matcher for the subset of semver ranges that package.json "engines.node" actually uses.
/// A range it cannot parse is reported as a problem instead of being assumed satisfied: the whole
/// point is to notice when an upstream core or dependency raises its Node floor above the runtime
/// this host ships, and that must surface as an explicit diagnostic rather than a start-up crash.
/// </summary>
public static class NodeEngineRange
{
    /// <summary>Returns true only when <paramref name="runningVersion"/> provably satisfies
    /// <paramref name="range"/>. Otherwise <paramref name="problem"/> explains whether the range was
    /// unsatisfied or unparseable.</summary>
    public static bool TrySatisfies(string range, string runningVersion, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(range) || !TryParse(runningVersion, out var running, out _, out _))
        {
            problem = "engines.node 版本范围或当前 Node 版本无效：" + range + " / " + runningVersion;
            return false;
        }

        var alternatives = range.Split("||", StringSplitOptions.TrimEntries);
        if (alternatives.Length == 0 || alternatives.Any(string.IsNullOrWhiteSpace))
        {
            problem = "engines.node 版本范围无效：" + range;
            return false;
        }

        foreach (var alternative in alternatives)
        {
            if (!TryMatch(alternative, running, out var satisfied, out var reason))
            {
                problem = "不支持的 engines.node 版本范围：" + range + "（" + reason + "）";
                return false;
            }

            if (satisfied) return true;
        }

        problem = "engines.node 要求 " + range + "，当前 Node " + runningVersion + " 不满足";
        return false;
    }

    private static bool TryMatch(string alternative, Version running, out bool satisfied, out string? reason)
    {
        satisfied = false;
        reason = null;
        // npm allows a space between an operator and its version (">= 14"), which would otherwise
        // split into two tokens.
        foreach (var op in new[] { ">=", "<=", ">", "<", "=", "^", "~" })
            alternative = alternative.Replace(op + " ", op, StringComparison.Ordinal);
        var tokens = alternative.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            reason = "空条件";
            return false;
        }

        foreach (var token in tokens)
        {
            if (token is "*" or "x" or "X")
            {
                satisfied = true;
                continue;
            }

            var text = token;
            var op = "=";
            foreach (var candidate in new[] { ">=", "<=", ">", "<", "=", "^", "~" })
            {
                if (text.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; text = text[candidate.Length..].Trim(); break; }
            }

            if (!TryParse(text, out var version, out var components, out var error))
            {
                reason = error;
                return false;
            }

            satisfied = op switch
            {
                ">=" => running >= version,
                ">" => running > version,
                "<=" => running <= version,
                "<" => running < version,
                // A partial version is a range, not an exact match: "22" means >=22.0.0 <23.0.0.
                "=" => components == 3 ? running == version : Within(running, version, Next(version, components)),
                "^" => Within(running, version, Next(version, components == 1 ? 1 : components == 2 ? 2 : (version.Major > 0 ? 1 : version.Minor > 0 ? 2 : 3))),
                "~" => Within(running, version, Next(version, components == 1 ? 1 : 2)),
                _ => throw new InvalidOperationException("未处理的比较符：" + op),
            };
            if (!satisfied) return true;
        }

        return true;
    }

    private static bool Within(Version running, Version low, Version high) => running >= low && running < high;

    /// <summary>Bumps the component <paramref name="index"/> (1 = major, 2 = minor, 3 = patch).</summary>
    private static Version Next(Version version, int index) => index switch
    {
        1 => new Version(version.Major + 1, 0, 0),
        2 => new Version(version.Major, version.Minor + 1, 0),
        3 => new Version(version.Major, version.Minor, version.Build + 1),
        _ => throw new InvalidOperationException("无效的版本位：" + index),
    };

    /// <summary>Accepts "22", "22.23", "v22.23.2" and nothing else. Pre-release and wildcard
    /// components are rejected so an unusual range cannot be silently treated as satisfied.</summary>
    private static bool TryParse(string text, out Version version, out int components, out string? error)
    {
        version = new Version(0, 0, 0);
        components = 0;
        error = null;
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            error = "不支持的版本写法：" + text;
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0)
            {
                error = "不支持的版本写法：" + text;
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        components = parts.Length;
        return true;
    }
}
