using System.Text;
using System.Text.RegularExpressions;

namespace DanmuApi.Platform;

public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null, string? appDataOverride = null)
    {
        Root = Path.GetFullPath(rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DanmuApi"));
        SettingsDirectory = Path.GetFullPath(appDataOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DanmuApi"));
    }

    public string Root { get; }
    public string RuntimeDirectory => Path.Combine(Root, "runtime");
    public string NodeProjectDirectory => Path.Combine(RuntimeDirectory, "nodejs-project");
    public string HostLogsDirectory => Path.Combine(Root, "logs");
    public string CoreCacheDirectory => Path.Combine(Root, "core-cache");
    public string SettingsDirectory { get; }
    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.properties");
    public string GithubTokenFile => Path.Combine(SettingsDirectory, "github-token.dat");
    public string AdminSessionFile => Path.Combine(SettingsDirectory, "admin-session.dat");
    public string IdentityFile => Path.Combine(SettingsDirectory, "instance-id");
    public string InstanceLockFile => Path.Combine(SettingsDirectory, "instance.lock");
    public string InstanceEndpointFile => Path.Combine(SettingsDirectory, "instance.endpoint");
    public string TrayLogFile => Path.Combine(HostLogsDirectory, "tray.log");
    public string LifecycleLogFile => Path.Combine(HostLogsDirectory, "lifecycle.log");
}

public interface ISettingsStore
{
    IReadOnlyDictionary<string, string> Read();
    void Write(IReadOnlyDictionary<string, string?> changes);
}

public sealed class SettingsStore : ISettingsStore
{
    private static readonly Regex KeyPattern = new("^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyDictionary<string, string> Read()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(_path, new UTF8Encoding(false, true));
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimStart('\uFEFF');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
            {
                continue;
            }

            var separator = FindSeparator(line);
            if (separator < 1)
            {
                throw new FormatException($"设置文件包含无效行: {_path}");
            }

            var key = Unescape(line[..separator].Trim());
            if (!KeyPattern.IsMatch(key))
            {
                throw new FormatException($"设置文件包含非法键: {key}");
            }

            result[key] = Unescape(line[(separator + 1)..].TrimStart());
        }

        return result;
    }

    public void Write(IReadOnlyDictionary<string, string?> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (var key in changes.Keys)
        {
            if (!KeyPattern.IsMatch(key))
            {
                throw new ArgumentException($"设置键非法: {key}", nameof(changes));
            }
        }

        var values = new Dictionary<string, string>(Read(), StringComparer.Ordinal);
        foreach (var (key, value) in changes)
        {
            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }
        }

        var directory = Path.GetDirectoryName(_path) ?? throw new IOException($"设置路径没有父目录: {_path}");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"{Path.GetFileName(_path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            var content = string.Join(Environment.NewLine, values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{Escape(pair.Key)}={Escape(pair.Value)}")) + Environment.NewLine;
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, _path, overwrite: true);
            var roundTrip = Read();
            foreach (var (key, expected) in values)
            {
                if (!roundTrip.TryGetValue(key, out var actual) || !string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    throw new IOException($"设置文件回读校验失败: {key}");
                }
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static int FindSeparator(string value)
    {
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
            }
            else if (character is '=' or ':' || char.IsWhiteSpace(character))
            {
                return index;
            }
        }

        return -1;
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '=' => "\\=",
                ':' => "\\:",
                _ => character.ToString(),
            });
        }

        return builder.ToString();
    }

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }

            builder.Append(value[++index] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'f' => '\f',
                '\\' => '\\',
                '=' => '=',
                ':' => ':',
                _ => value[index],
            });
        }

        return builder.ToString();
    }
}
