using System.Text.Json;
using System.Text.Json.Serialization;

namespace DanmuApi.Platform;

/// <summary>App-owned transport settings; independent of the core's secret .env file.</summary>
public sealed record OutboundSettings(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources,
    [property: JsonPropertyName("httpVersion")] string HttpVersion,
    [property: JsonPropertyName("dohUrl")] string DohUrl,
    [property: JsonPropertyName("connectTimeoutMs")] int ConnectTimeoutMs,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion = 1)
{
    public static OutboundSettings Default => new(false,
        Array.AsReadOnly(new[] { "bahamut", "tmdb", "dandan", "animeko" }), "auto", "", 3000);

    public static void Validate(OutboundSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.SchemaVersion != 1) throw new FormatException("出站设置 schemaVersion 必须为 1。");
        if (settings.Sources is null) throw new FormatException("出站设置 sources 必须是数组。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in settings.Sources)
        {
            if (source is not ("bahamut" or "tmdb" or "dandan" or "animeko"))
                throw new FormatException("出站设置 sources 包含未知来源。");
            if (!seen.Add(source)) throw new FormatException("出站设置 sources 不允许重复来源。");
        }
        if (settings.Enabled && seen.Count == 0)
            throw new FormatException("启用增强直连时至少选择一个来源。");
        if (settings.HttpVersion is not ("auto" or "h2" or "h3"))
            throw new FormatException("出站设置 httpVersion 只能为 auto、h2 或 h3。");
        if (settings.ConnectTimeoutMs is < 1 or > 60000)
            throw new FormatException("出站设置 connectTimeoutMs 必须是 1 到 60000 的整数。");
        if (settings.DohUrl is null) throw new FormatException("出站设置 dohUrl 必须是字符串。");
        if (settings.DohUrl.Length != 0 &&
            (settings.DohUrl.Length > 8192 || settings.DohUrl.Any(char.IsWhiteSpace) ||
             !Uri.TryCreate(settings.DohUrl, UriKind.Absolute, out var uri) ||
             uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) ||
             !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
             settings.DohUrl.Contains('#') || settings.DohUrl.Contains('\\')))
            throw new FormatException("出站设置 dohUrl 必须为 HTTPS 地址，不允许凭据、片段或空白；留空使用内置 DoH。");
    }

    public static bool Equivalent(OutboundSettings left, OutboundSettings right) =>
        left.SchemaVersion == right.SchemaVersion && left.Enabled == right.Enabled &&
        left.HttpVersion == right.HttpVersion && left.DohUrl == right.DohUrl &&
        left.ConnectTimeoutMs == right.ConnectTimeoutMs && left.Sources.Count == right.Sources.Count &&
        new HashSet<string>(left.Sources, StringComparer.Ordinal).SetEquals(right.Sources);

    public static OutboundSettings Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("出站设置必须是 JSON 对象。");
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (!fields.Add(field.Name)) throw new FormatException("出站设置不允许重复字段。");
            if (field.Name is not ("schemaVersion" or "enabled" or "sources" or "httpVersion" or "dohUrl" or "connectTimeoutMs"))
                throw new FormatException("出站设置包含未知字段。");
        }
        JsonElement Field(string name, JsonValueKind kind)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != kind)
                throw new FormatException($"出站设置 {name} 缺失或类型错误。");
            return value;
        }
        int Integer(string name)
        {
            if (!Field(name, JsonValueKind.Number).TryGetInt32(out var value))
                throw new FormatException($"出站设置 {name} 必须是整数。");
            return value;
        }
        if (!root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException("出站设置 enabled 缺失或类型错误。");
        var sources = Field("sources", JsonValueKind.Array).EnumerateArray().Select(value =>
            value.ValueKind == JsonValueKind.String ? value.GetString()! :
            throw new FormatException("出站设置 sources 只能包含字符串。")).ToArray();
        var settings = new OutboundSettings(enabled.GetBoolean(), Array.AsReadOnly(sources),
            Field("httpVersion", JsonValueKind.String).GetString()!,
            Field("dohUrl", JsonValueKind.String).GetString()!, Integer("connectTimeoutMs"), Integer("schemaVersion"));
        Validate(settings);
        return settings;
    }
}

public interface IOutboundSettingsStore
{
    string DirectoryPath { get; }
    string SettingsPath { get; }
    OutboundSettings Read();
    void Write(OutboundSettings settings);
}

/// <summary>Strict UTF-8 JSON storage with same-volume atomic replacement and readback verification.</summary>
public sealed class OutboundSettingsStore : IOutboundSettingsStore
{
    public const int MaxDocumentBytes = 1_048_576;
    private readonly Action<string>? _diagnostic;
    private readonly object _gate = new();
    private string? _lastDiagnostic;

    public OutboundSettingsStore(AppPaths paths, Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        DirectoryPath = Path.Combine(paths.NodeProjectDirectory, "config", "outbound");
        SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        _diagnostic = diagnostic;
    }

    public string DirectoryPath { get; }
    public string SettingsPath { get; }
    public string? LastDiagnostic => Volatile.Read(ref _lastDiagnostic);

    public OutboundSettings Read()
    {
        lock (_gate)
        {
            try { return ReadCore(); }
            catch (Exception error)
            {
                RecordFailure("读取出站设置失败", error);
                throw;
            }
        }
    }

    private OutboundSettings ReadCore()
    {
        FileStream stream;
        try { stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (FileNotFoundException) { return OutboundSettings.Default; }
        catch (DirectoryNotFoundException) { return OutboundSettings.Default; }
        using (stream)
        {
            if (stream.Length > MaxDocumentBytes) throw new FormatException("出站设置超过 1 MiB 限制。");
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int count;
            while ((count = stream.Read(bytes)) != 0)
            {
                if (buffer.Length + count > MaxDocumentBytes) throw new FormatException("出站设置超过 1 MiB 限制。");
                buffer.Write(bytes, 0, count);
            }
            try
            {
                using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
                return OutboundSettings.Parse(document.RootElement);
            }
            catch (JsonException error)
            {
                // Never include document contents (including a pasted credential) in diagnostics.
                throw new FormatException($"出站设置不是有效 JSON（行 {error.LineNumber}，字节 {error.BytePositionInLine}）。");
            }
        }
    }

    public void Write(OutboundSettings settings)
    {
        lock (_gate)
        {
            string? temporary = null;
            Exception? failure = null;
            try
            {
                OutboundSettings.Validate(settings);
                var frozen = settings with { Sources = Array.AsReadOnly(settings.Sources.ToArray()) };
                var content = JsonSerializer.SerializeToUtf8Bytes(frozen);
                if (content.Length > MaxDocumentBytes) throw new FormatException("出站设置超过 1 MiB 限制。");
                Directory.CreateDirectory(DirectoryPath);
                temporary = Path.Combine(DirectoryPath, $"settings.json.tmp-{Guid.NewGuid():N}");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, SettingsPath, overwrite: true);
                var actual = ReadCore();
                if (!OutboundSettings.Equivalent(frozen, actual))
                    throw new IOException("出站设置回读校验失败。");
            }
            catch (Exception error)
            {
                failure = error;
                RecordFailure("保存出站设置失败", error);
                throw;
            }
            finally
            {
                if (temporary is not null)
                {
                    try { File.Delete(temporary); }
                    catch (Exception cleanupError)
                    {
                        RecordFailure("清理出站设置临时文件失败", cleanupError);
                        if (failure is null) throw;
                        throw new AggregateException("出站设置保存与临时文件清理均失败。", failure, cleanupError);
                    }
                }
            }
        }
    }

    private void RecordFailure(string operation, Exception error)
    {
        var message = $"{operation}：{error.GetType().Name}；{error.Message}";
        Volatile.Write(ref _lastDiagnostic, message);
        _diagnostic?.Invoke(message);
    }
}
