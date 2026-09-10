using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DanmuApi.Core;

public static class CoreManifestStore
{
    public const string ManifestFileName = ".danmuapi-core-source.json";
    private const int MaxManifestBytes = 64 * 1024;

    public static void Write(string coreDirectory, CoreInstallationManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coreDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        var directory = Path.GetFullPath(coreDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ManifestFileName);
        var temporary = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", manifest.SchemaVersion);
                writer.WriteString("variant", manifest.Variant.ToStorageKey());
                writer.WriteString("repository", manifest.Repository);
                writer.WriteString("branch", manifest.Branch);
                writer.WriteString("commitSha", manifest.CommitSha);
                if (manifest.Version is not null)
                {
                    writer.WriteString("version", manifest.Version);
                }
                writer.WriteString("displayName", manifest.DisplayName);
                writer.WriteString("installKind", manifest.InstallKind.ToString());
                if (manifest.PullRequestNumber is not null)
                {
                    writer.WriteNumber("pullRequestNumber", manifest.PullRequestNumber.Value);
                }
                writer.WriteString("installedAt", manifest.InstalledAt.ToUniversalTime());
                writer.WriteEndObject();
            }

            File.Move(temporary, path, overwrite: true);
            var roundTrip = Read(directory);
            if (roundTrip != manifest with { InstalledAt = manifest.InstalledAt.ToUniversalTime() })
            {
                throw new IOException("核心来源 manifest 回读校验失败");
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

    public static CoreInstallationManifest? Read(string coreDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coreDirectory);
        var path = Path.Combine(Path.GetFullPath(coreDirectory), ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaxManifestBytes)
        {
            throw new IOException($"核心来源 manifest 大小无效：{path}");
        }

        string json;
        try
        {
            json = File.ReadAllText(path, new UTF8Encoding(false, true));
        }
        catch (DecoderFallbackException error)
        {
            throw new IOException("核心来源 manifest 不是有效 UTF-8", error);
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("manifest 根节点必须是对象");
            }

            var root = document.RootElement;
            var schema = RequiredInt(root, "schemaVersion");
            var manifest = new CoreInstallationManifest(
                schema,
                ManagedCoreVariantExtensions.ParseManagedVariant(RequiredString(root, "variant")),
                RequiredString(root, "repository"),
                RequiredString(root, "branch"),
                RequiredString(root, "commitSha"),
                OptionalString(root, "version"),
                RequiredString(root, "displayName"),
                Enum.TryParse<CoreInstallKind>(RequiredString(root, "installKind"), out var kind)
                    ? kind
                    : throw new JsonException("manifest installKind 无效"),
                OptionalInt(root, "pullRequestNumber"),
                DateTimeOffset.TryParse(
                    RequiredString(root, "installedAt"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var installedAt)
                    ? installedAt.ToUniversalTime()
                    : throw new JsonException("manifest installedAt 无效"));
            Validate(manifest);
            return manifest;
        }
        catch (JsonException error)
        {
            throw new IOException($"核心来源 manifest 无效：{error.Message}", error);
        }
    }

    private static void Validate(CoreInstallationManifest manifest)
    {
        if (manifest.SchemaVersion != CoreInstallationManifest.CurrentSchemaVersion)
        {
            throw new IOException($"不支持的核心来源 manifest 版本：{manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)}");
        }

        var repository = GithubRepositoryReference.Parse(manifest.Repository);
        if (repository.Branch is not null || !string.Equals(repository.FullName, manifest.Repository, StringComparison.Ordinal))
        {
            throw new IOException("核心来源 manifest repository 必须是规范 owner/repo");
        }

        GithubRepositoryReference.ValidateBranch(manifest.Branch);
        if (manifest.CommitSha.Length is < 7 or > 64 || !manifest.CommitSha.All(Uri.IsHexDigit))
        {
            throw new IOException("核心来源 manifest commitSha 无效");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 80)
        {
            throw new IOException("核心来源 manifest displayName 无效");
        }

        if (manifest.PullRequestNumber is <= 0)
        {
            throw new IOException("核心来源 manifest PR 编号无效");
        }
    }

    private static JsonElement RequiredProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value : throw new JsonException($"manifest 缺少字段：{name}");

    private static string RequiredString(JsonElement root, string name)
    {
        var value = RequiredProperty(root, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"manifest 字段 {name} 必须是非空字符串");
        }
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new JsonException($"manifest 字段 {name} 必须是字符串或 null");
    }

    private static int RequiredInt(JsonElement root, string name) =>
        OptionalInt(root, name) ?? throw new JsonException($"manifest 缺少整数字段：{name}");

    private static int? OptionalInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : throw new JsonException($"manifest 字段 {name} 必须是整数或 null");
    }
}
