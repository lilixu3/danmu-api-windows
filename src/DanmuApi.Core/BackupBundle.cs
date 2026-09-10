using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DanmuApi.Core;

public sealed record BackupDocument(int SchemaVersion, long CreatedAtMs, string AppVersion,
    IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> OmittedSections, int ExcludedKeys,
    string? Favorites, IReadOnlyDictionary<string, string>? DesktopPreferences);

public static class BackupDesktopPreferencePolicy
{
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal) { "theme", "notification_level", "close_action" };
    public static void Validate(string key, string value)
    {
        var allowed = key switch
        {
            "theme" => new[] { "system", "light", "dark" },
            "close_action" => new[] { "ask", "exit", "tray" },
            "notification_level" => new[] { "all", "updates", "startup_success", "off" },
            _ => Array.Empty<string>()
        };
        if (!allowed.Contains(value, StringComparer.Ordinal)) throw new InvalidDataException("桌面备份包含未知或无效偏好");
    }
}

/// <summary>Mobile schema v1/v2 transport. Only Environment is applied on Windows.</summary>
public static class BackupBundle
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const string Format = "danmu-api-app-backup";
    public const string Warning = "备份为未加密 JSON，配置可能包含地址、账号及业务隐私；仅保存到您授权的本地位置或 HTTPS WebDAV。已排除已知秘密及宿主身份，但不能保证自定义配置不含隐私。";
    private static readonly Regex Key = new("\\A[A-Za-z_][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Secret = new("TOKEN|PASSWORD|PASSWD|SECRET|API[_-]?KEY|COOKIE|AUTH|SESSION|PRIVATE|_KEY$|URL|URI", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Sections = ["Environment", "Favorites", "AppSettings", "CoreSources", "AccessRules"];
    public static bool IsTransferable(string key) => Key.IsMatch(key) && !Secret.IsMatch(key) &&
        !key.StartsWith("DANMU_API_", StringComparison.OrdinalIgnoreCase) &&
        !new[] { "HOME", "TEMP", "TMP", "NODE_COMPILE_CACHE", "COMPUTERNAME", "USERPROFILE" }.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static BackupDocument Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("备份超过 16 MiB 上限");
        using var json = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes), new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        RejectDuplicates(root);
        if (root.GetProperty("format").GetString() != Format) throw new InvalidDataException("不是弹幕 App 备份");
        var schema = root.GetProperty("schemaVersion").GetInt32();
        if (schema is < 1 or > 2) throw new InvalidDataException("仅支持备份 schema 1–2");
        var timestamp = root.GetProperty("createdAtMs").GetInt64();
        _ = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        var version = root.GetProperty("appVersion").GetString() ?? throw new InvalidDataException("缺少应用版本");
        var sections = root.GetProperty("sections").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        if (sections.Length == 0 || sections.Distinct().Count() != sections.Length || sections.Any(x => !Sections.Contains(x)))
            throw new InvalidDataException("备份类别无效或重复");
        if (!sections.Contains("Environment")) throw new InvalidDataException("本版本仅恢复 Environment，此备份未包含该部分");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var excluded = 0;
        foreach (var entry in root.GetProperty("environment").EnumerateObject())
        {
            if (!Key.IsMatch(entry.Name) || entry.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("环境变量格式无效");
            var value = entry.Value.GetString()!;
            if (value.Contains('\0')) throw new InvalidDataException("环境变量含 NUL");
            if (!IsTransferable(entry.Name)) { excluded++; continue; }
            if (!values.TryAdd(entry.Name, value)) throw new InvalidDataException("环境变量名称重复");
        }
        string? favorites = null;
        if (sections.Contains("Favorites"))
            favorites = NormalizeFavorites(root.GetProperty("favorites").GetString() ?? throw new InvalidDataException("缺少收藏数据"));
        Dictionary<string, string>? desktop = null;
        if (root.TryGetProperty("desktopPreferences", out var preferences) && preferences.ValueKind != JsonValueKind.Null)
        {
            desktop = new(StringComparer.Ordinal);
            foreach (var item in preferences.EnumerateObject())
            {
                var value = item.Value.GetString() ?? throw new InvalidDataException("桌面偏好值不能为空");
                BackupDesktopPreferencePolicy.Validate(item.Name, value);
                desktop.Add(item.Name, value);
            }
        }
        return new(schema, timestamp, version, values, sections.Where(x => x is not "Environment" and not "Favorites").ToArray(), excluded, favorites, desktop);
    }

    public static string NormalizeFavorites(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > 8 * 1024 * 1024) throw new InvalidDataException("收藏超过 8 MiB");
        for (var depth = 0; depth < 3; depth++)
        {
            using var json = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.String && depth < 2) { raw = root.GetString()!; continue; }
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("收藏必须是 JSON 对象");
            RejectDuplicates(root);
            var items = root.EnumerateObject().ToArray();
            if (items.Length > 10000 || items.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Value.ValueKind != JsonValueKind.Object))
                throw new InvalidDataException("收藏条目无效或数量超过 10000");
            return root.GetRawText() + "\n";
        }
        throw new InvalidDataException("收藏编码层数无效");
    }

    public static byte[] Encode(IReadOnlyDictionary<string, string> values, string appVersion,
        string? favorites = null, IReadOnlyDictionary<string, string>? desktopPreferences = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = Format, schemaVersion = 2, createdAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), appVersion,
            sections = new[] { "Environment" }
                .Concat(favorites is null ? Array.Empty<string>() : ["Favorites"])
                .Concat(desktopPreferences is null ? Array.Empty<string>() : ["AppSettings"])
                .ToArray(),
            environment = values.Where(x => IsTransferable(x.Key)).ToDictionary(x => x.Key, x => x.Value),
            favorites, desktopPreferences, appPreferences = Array.Empty<object>(), corePreferences = Array.Empty<object>(),
            coreInventory = Array.Empty<object>(), accessRules = (string?)null
        }, new JsonSerializerOptions { WriteIndented = true });
        _ = Decode(bytes);
        return bytes;
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (result.Length + count > MaximumBytes) throw new InvalidDataException("备份超过 16 MiB 上限");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("JSON 包含重复字段");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
