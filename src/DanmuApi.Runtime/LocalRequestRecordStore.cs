using System.Text;
using System.Text.Json;

namespace DanmuApi.Runtime;

public sealed record LocalRequestRecord(
    long Id,
    DateTimeOffset Timestamp,
    string Scene,
    string Method,
    string Interface,
    string Parameters,
    int? StatusCode,
    long DurationMilliseconds,
    bool Success,
    string? ErrorMessage,
    string ResponseSummary);

public interface ILocalRequestRecordStore
{
    long AllocateId();
    IReadOnlyList<LocalRequestRecord> Snapshot();
    void Add(LocalRequestRecord record);
    void Clear();
}

public sealed class LocalRequestRecordStore : ILocalRequestRecordStore
{
    public const int MaximumRecords = 200;

    private readonly object _sync = new();
    private readonly List<LocalRequestRecord> _records = [];
    private long _nextId;

    public long AllocateId()
    {
        lock (_sync)
        {
            return ++_nextId;
        }
    }

    public IReadOnlyList<LocalRequestRecord> Snapshot()
    {
        lock (_sync)
        {
            return _records.OrderByDescending(record => record.Timestamp).ToArray();
        }
    }

    public void Add(LocalRequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_sync)
        {
            if (record.Id > _nextId)
            {
                _nextId = record.Id;
            }

            var existing = _records.FindIndex(item => item.Id == record.Id);
            if (existing >= 0)
            {
                _records[existing] = record;
                return;
            }

            _records.Add(record);
            if (_records.Count > MaximumRecords)
            {
                _records.RemoveRange(0, _records.Count - MaximumRecords);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _records.Clear();
        }
    }
}

public static class DanmuRequestSceneContext
{
    private static readonly AsyncLocal<string?> CurrentScene = new();

    internal static string? Current => CurrentScene.Value;

    public static IDisposable Push(string scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scene);
        var previous = CurrentScene.Value;
        CurrentScene.Value = scene.Trim();
        return new SceneScope(previous);
    }

    private sealed class SceneScope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentScene.Value = previous;
        }
    }
}

public static class RequestRecordRedactor
{
    public static string MaskInterface(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var pathAndQuery = endpoint.PathAndQuery;
        var apiIndex = pathAndQuery.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        return MaskInterface(apiIndex >= 0 ? pathAndQuery[apiIndex..] : endpoint.AbsolutePath);
    }

    public static string MaskInterface(string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathAndQuery);
        var normalized = pathAndQuery.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https")
        {
            normalized = absolute.PathAndQuery;
        }

        var apiIndex = normalized.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        if (apiIndex >= 0)
        {
            normalized = normalized[apiIndex..];
        }
        else
        {
            var danmakuIndex = normalized.IndexOf("/danmaku", StringComparison.OrdinalIgnoreCase);
            if (danmakuIndex >= 0)
            {
                normalized = normalized[danmakuIndex..];
            }
        }

        var question = normalized.IndexOf('?');
        if (question < 0)
        {
            return normalized;
        }

        var path = normalized[..question];
        var query = normalized[(question + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var equals = part.IndexOf('=');
                var name = equals < 0 ? part : part[..equals];
                return $"{name}=***";
            });
        return path + "?" + string.Join('&', query);
    }

    public static string MaskResponseSummary(ReadOnlySpan<byte> body, string contentType)
    {
        if (body.IsEmpty)
        {
            return "<空响应>";
        }

        if (!contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                ? "<XML 响应内容未保存>"
                : contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    ? "<文本响应内容未保存>"
                    : "<二进制响应内容未保存>";
        }

        try
        {
            return MaskJsonValues(new UTF8Encoding(false, true).GetString(body));
        }
        catch (DecoderFallbackException)
        {
            return "<JSON 响应不是有效 UTF-8，内容未保存>";
        }
    }

    public static string MaskJsonValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteMasked(document.RootElement, writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return "<请求体不是有效 JSON，内容未保存>";
        }
    }

    private static void WriteMasked(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteMasked(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteMasked(item, writer);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue("***");
                break;
        }
    }
}
