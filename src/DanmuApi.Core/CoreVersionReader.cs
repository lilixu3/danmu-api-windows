using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanmuApi.Core;

public sealed record CoreVersionInfo(
    string Variant,
    string Directory,
    bool Installed,
    string? Version,
    string? Diagnostic);

public static partial class CoreVersionReader
{
    private const long MaxMetadataBytes = 1_048_576;

    public static CoreVersionInfo Inspect(string nodeProjectDirectory, string variant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeProjectDirectory);
        ValidateVariant(variant);
        var directory = Path.Combine(Path.GetFullPath(nodeProjectDirectory), $"danmu_api_{variant.ToLowerInvariant()}");
        var installed = File.Exists(Path.Combine(directory, "worker.js"));
        if (!installed)
        {
            return new(variant.ToLowerInvariant(), directory, false, null, null);
        }

        try
        {
            return new(variant.ToLowerInvariant(), directory, true, ReadVersion(directory), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(variant.ToLowerInvariant(), directory, true, null, error.Message);
        }
    }

    public static string? ReadVersion(string coreDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coreDirectory);
        var directory = Path.GetFullPath(coreDirectory);
        foreach (var relativePath in new[] { Path.Combine("configs", "globals.js"), Path.Combine("config", "globals.js") })
        {
            var path = Path.Combine(directory, relativePath);
            if (!File.Exists(path))
            {
                continue;
            }

            var content = ReadBoundedText(path);
            var match = VersionPattern().Match(content);
            if (match.Success)
            {
                var value = match.Groups[1].Value.Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        var packagePath = Path.Combine(directory, "package.json");
        if (!File.Exists(packagePath))
        {
            return null;
        }

        var packageContent = ReadBoundedText(packagePath);
        using var document = JsonDocument.Parse(packageContent, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("核心 package.json 根节点必须是对象");
        }

        if (!document.RootElement.TryGetProperty("version", out var version) || version.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (version.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("核心 package.json 的 version 必须是字符串");
        }

        return string.IsNullOrWhiteSpace(version.GetString()) ? null : version.GetString()!.Trim();
    }

    private static string ReadBoundedText(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxMetadataBytes)
        {
            throw new IOException($"核心版本元数据超过 1MB: {path}");
        }

        return File.ReadAllText(path, new System.Text.UTF8Encoding(false, true));
    }

    private static void ValidateVariant(string variant)
    {
        if (variant is not ("stable" or "dev" or "custom"))
        {
            throw new ArgumentException("核心变体必须是 stable、dev 或 custom", nameof(variant));
        }
    }

    [GeneratedRegex("\\b(?:VERSION|version)\\s*[:=]\\s*['\"]([^'\"]+)['\"]", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
