using System.Globalization;
using System.Reflection;

namespace DanmuApi.Core;

/// <summary>
/// The Node.js version this host was built against. The value is declared once in
/// <c>Directory.Build.props</c> and compiled into this assembly, so the deployed node.exe can be
/// checked against it. A bundle carrying a different runtime must fail explicitly instead of
/// running an unverified one.
/// </summary>
public static class BundledNodeRuntime
{
    public const string MetadataKey = "DanmuBundledNodeVersion";

    public static string ExpectedVersion { get; } = ReadDeclaredVersion();

    /// <summary>Major version of <see cref="ExpectedVersion"/>.</summary>
    public static int ExpectedMajor { get; } = ParseMajor(ExpectedVersion);

    private static string ReadDeclaredVersion()
    {
        var value = typeof(BundledNodeRuntime).Assembly
            .GetCustomAttributes(typeof(AssemblyMetadataAttribute), inherit: false)
            .OfType<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, MetadataKey, StringComparison.Ordinal))
            ?.Value?.Trim();
        if (string.IsNullOrEmpty(value)) throw new InvalidOperationException($"{MetadataKey} 未编译进程序集。");
        ParseMajor(value);
        return value;
    }

    /// <summary>Parses "22", "v22", "22.23.2" or "v22.23.2" into the major version.</summary>
    public static int ParseMajor(string version)
    {
        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var head = text.Split('.', 2)[0];
        if (!int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major <= 0)
            throw new InvalidOperationException("无效的 Node 版本号：" + version);
        return major;
    }
}
