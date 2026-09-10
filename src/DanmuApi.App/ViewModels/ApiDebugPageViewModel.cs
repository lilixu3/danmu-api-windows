using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public enum ApiParameterKind
{
    Query,
    Path,
    Json,
}

public sealed record ApiParameterDefinition(
    string Name,
    string Label,
    ApiParameterKind Kind,
    bool Required,
    string Placeholder = "",
    IReadOnlyList<string>? Options = null)
{
    public bool IsSelect => Options is { Count: > 0 };
}

public sealed partial class ApiParameterValueViewModel : ViewModelBase
{
    public ApiParameterValueViewModel(ApiParameterDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Value = string.Empty;
    }

    public ApiParameterDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Label => Definition.Required ? $"{Definition.Label} *" : Definition.Label;
    public ApiParameterKind Kind => Definition.Kind;
    public bool IsSelect => Definition.IsSelect;
    public IReadOnlyList<string> Options => Definition.Options ?? [];
    public string Placeholder => Definition.Placeholder;

    [ObservableProperty]
    private string _value;
}

public sealed record ApiDefinition(
    string Key,
    string Name,
    HttpMethod Method,
    string Path,
    IReadOnlyList<ApiParameterDefinition> Parameters,
    bool HasBody = false)
{
    public override string ToString() => Name;
}

public sealed partial class ApiDebugPageViewModel : ViewModelBase, IAsyncDisposable
{
    private const int PreviewLimit = 4000;
    private readonly RuntimeApiContext _context;
    private readonly IDanmuApiClient _client;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private CancellationTokenSource _requestCts = new();
    private byte[] _rawResponseBytes = [];
    private int _requestSequence;

    [ObservableProperty]
    private ApiDefinition _selectedApi = null!;

    [ObservableProperty]
    private string _jsonBody = "{\n  \"type\": \"qq\",\n  \"segment_start\": 0,\n  \"segment_end\": 30000,\n  \"url\": \"https://example.invalid/segment\"\n}";

    [ObservableProperty]
    private string _responseText = string.Empty;

    [ObservableProperty]
    private string _responsePreview = string.Empty;

    [ObservableProperty]
    private string _requestPreview = string.Empty;

    [ObservableProperty]
    private string _curlText = string.Empty;

    [ObservableProperty]
    private string? _diagnostic;

    [ObservableProperty]
    private string _responseMeta = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isResponseExpanded;

    public ApiDebugPageViewModel(RuntimeApiContext context, IDanmuApiClient client, IUiDialogService dialogs, IAppDiagnostics diagnostics)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        Definitions =
        [
            new("searchAnime", "搜索动漫", HttpMethod.Get, "/api/v2/search/anime", [
                new("keyword", "关键词或播放链接 URL", ApiParameterKind.Query, true, "示例：生万物 或 https://...")]),
            new("searchEpisodes", "搜索剧集", HttpMethod.Get, "/api/v2/search/episodes", [
                new("anime", "动漫名称", ApiParameterKind.Query, true, "示例：生万物"),
                new("episode", "集（可选）", ApiParameterKind.Query, false, "示例：1 或 movie")]),
            new("matchAnime", "匹配动漫", HttpMethod.Post, "/api/v2/match", [
                new("fileName", "文件名", ApiParameterKind.Json, true, "示例：生万物 S02E08")], true),
            new("getBangumi", "获取番剧详情", HttpMethod.Get, "/api/v2/bangumi/:animeId", [
                new("animeId", "动漫 ID", ApiParameterKind.Path, true, "示例：236379")]),
            new("getComment", "获取弹幕", HttpMethod.Get, "/api/v2/comment/:commentId", [
                new("commentId", "弹幕 ID", ApiParameterKind.Path, true, "示例：10009"),
                new("format", "格式", ApiParameterKind.Query, false, Options: ["json", "xml"]),
                new("duration", "附带时长", ApiParameterKind.Query, false, Options: ["true", "false"]),
                new("segmentflag", "分片标志", ApiParameterKind.Query, false, Options: ["true", "false"])]),
            new("getCommentByUrl", "URL 解析弹幕", HttpMethod.Get, "/api/v2/comment", [
                new("url", "视频 URL", ApiParameterKind.Query, true, "必须是 http 或 https URL"),
                new("format", "格式", ApiParameterKind.Query, false, Options: ["json", "xml"]),
                new("duration", "附带时长", ApiParameterKind.Query, false, Options: ["true", "false"]),
                new("segmentflag", "分片标志", ApiParameterKind.Query, false, Options: ["true", "false"])]),
            new("getSegmentComment", "获取分片弹幕", HttpMethod.Post, "/api/v2/segmentcomment", [
                new("format", "格式", ApiParameterKind.Query, false, Options: ["json", "xml"])], true),
            new("fongmiGet", "兼容 FongMi · GET", HttpMethod.Get, "/api/v2/fongmi/danmaku", CompatibilityParameters(ApiParameterKind.Query)),
            new("fongmiPost", "兼容 FongMi · JSON POST", HttpMethod.Post, "/api/v2/fongmi/danmaku", CompatibilityParameters(ApiParameterKind.Json), true),
            new("danmakuGet", "兼容短地址 · GET", HttpMethod.Get, "/danmaku", CompatibilityParameters(ApiParameterKind.Query)),
            new("danmakuPost", "兼容短地址 · JSON POST", HttpMethod.Post, "/danmaku", CompatibilityParameters(ApiParameterKind.Json), true),
        ];
        _selectedApi = Definitions[0];

        static IReadOnlyList<ApiParameterDefinition> CompatibilityParameters(ApiParameterKind kind) =>
        [
            new("name", "动漫名称", kind, true, "示例：生万物"),
            new("episode", "集（可选）", kind, false, "示例：1 或 movie"),
        ];
        RebuildParameterValues();
    }

    public IReadOnlyList<ApiDefinition> Definitions { get; }
    public ObservableCollection<ApiParameterValueViewModel> ParameterValues { get; } = [];
    public bool HasRawBody => SelectedApi.Key == "getSegmentComment";
    public bool HasResponse => ResponseText.Length > 0;
    public bool HasRequestPreview => RequestPreview.Length > 0;
    public bool HasCurl => CurlText.Length > 0;
    public bool IsCommentApi => SelectedApi.Key is "getComment" or "getCommentByUrl" or "getSegmentComment";
    public bool CanCancel => IsBusy;
    public string ResponseDisplay => IsResponseExpanded ? ResponseText : ResponsePreview;
    public string RequestSummary => $"{SelectedApi.Method.Method} {SelectedApi.Path}";
    public string ToggleResponseText => IsResponseExpanded ? "收起完整响应" : "展开完整响应";
    internal int RawResponseByteCount => _rawResponseBytes.Length;

    partial void OnSelectedApiChanged(ApiDefinition value)
    {
        _requestCts.Cancel();
        Interlocked.Increment(ref _requestSequence);
        IsBusy = false;
        RebuildParameterValues();
        ClearResponse();
        OnPropertyChanged(nameof(HasRawBody));
        OnPropertyChanged(nameof(IsCommentApi));
        OnPropertyChanged(nameof(RequestSummary));
        OnPropertyChanged(nameof(CanCancel));
    }

    partial void OnResponseTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasResponse));
        OnPropertyChanged(nameof(ResponseDisplay));
    }

    partial void OnResponsePreviewChanged(string value) => OnPropertyChanged(nameof(ResponseDisplay));
    partial void OnRequestPreviewChanged(string value) => OnPropertyChanged(nameof(HasRequestPreview));
    partial void OnCurlTextChanged(string value) => OnPropertyChanged(nameof(HasCurl));
    partial void OnIsResponseExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ResponseDisplay));
        OnPropertyChanged(nameof(ToggleResponseText));
    }

    private void RebuildParameterValues()
    {
        ParameterValues.Clear();
        foreach (var definition in SelectedApi.Parameters)
        {
            var value = new ApiParameterValueViewModel(definition);
            value.Value = definition.Name switch
            {
                "format" => "json",
                "duration" => "true",
                _ => string.Empty,
            };
            ParameterValues.Add(value);
        }
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        _requestCts.Cancel();
        _requestCts.Dispose();
        _requestCts = new CancellationTokenSource();
        var sequence = Interlocked.Increment(ref _requestSequence);
        var token = _requestCts.Token;
        try
        {
            _context.EnsureRunning();
            var values = ReadParameters();
            var jsonBody = BuildRequestBody(values);
            IsBusy = true;
            Diagnostic = null;
            ClearResponse();
            var result = await _client.SendRawAsync(
                _context.Host,
                _context.Port!.Value,
                _context.Token,
                SelectedApi.Key,
                values,
                jsonBody,
                token).ConfigureAwait(true);
            if (sequence != _requestSequence || token.IsCancellationRequested)
            {
                return;
            }

            RequestPreview = $"{SelectedApi.Method.Method} {RedactRequestPath(result.RequestPath)}";
            CurlText = BuildCurl(result.RequestPath, jsonBody);
            ReplaceRawResponse(result.Body);
            try
            {
                ResponseText = FormatResponse(result.BodyText, result.ContentType, _context.Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(result.Body);
            }
            ResponsePreview = ResponseText.Length > PreviewLimit ? ResponseText[..PreviewLimit] : ResponseText;
            ResponseMeta = $"HTTP {result.StatusCode} · {result.Duration.TotalMilliseconds:0} ms · {result.ByteCount:N0} bytes";
            Diagnostic = result.IsSuccessStatusCode
                ? "请求完成。界面和复制仅提供完整脱敏响应副本。"
                : $"HTTP {result.StatusCode}。界面和复制仅提供完整脱敏错误响应副本。";
            OnPropertyChanged(nameof(CanCancel));
        }
        catch (Exception error) when (error is DanmuApiException or ArgumentException or FormatException or JsonException)
        {
            if (sequence != _requestSequence)
            {
                return;
            }

            if (error is DanmuApiException { Kind: DanmuApiFailureKind.Cancelled })
            {
                Diagnostic = "请求已取消。";
                return;
            }

            Diagnostic = error.Message;
            _diagnostics.Record("接口调试失败", error);
        }
        finally
        {
            if (sequence == _requestSequence)
            {
                IsBusy = false;
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    [RelayCommand]
    private void Cancel() => _requestCts.Cancel();

    [RelayCommand]
    private async Task CopyResponseAsync()
    {
        if (HasResponse)
        {
            await _dialogs.CopyTextAsync(ResponseText).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CopyCurlAsync()
    {
        if (!string.IsNullOrWhiteSpace(CurlText))
        {
            await _dialogs.CopyTextAsync(CurlText).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void ClearResponse()
    {
        ClearRawResponse();
        ResponseText = string.Empty;
        ResponsePreview = string.Empty;
        RequestPreview = string.Empty;
        CurlText = string.Empty;
        ResponseMeta = string.Empty;
        IsResponseExpanded = false;
        OnPropertyChanged(nameof(HasResponse));
    }

    [RelayCommand]
    private void ToggleResponse() => IsResponseExpanded = !IsResponseExpanded;

    private IReadOnlyDictionary<string, string?> ReadParameters()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in ParameterValues)
        {
            var value = item.Value.Trim();
            if (item.Definition.Required && value.Length == 0)
            {
                throw new ArgumentException($"请输入{item.Definition.Label}");
            }

            values[item.Name] = value.Length == 0 ? null : value;
        }

        return values;
    }

    private string? BuildRequestBody(IReadOnlyDictionary<string, string?> values)
    {
        if (!SelectedApi.HasBody)
        {
            return null;
        }

        if (SelectedApi.Key == "getSegmentComment")
        {
            using var document = JsonDocument.Parse(JsonBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("分片请求体必须是 JSON 对象");
            }
            return document.RootElement.GetRawText();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            if (SelectedApi.Key is "fongmiPost" or "danmakuPost")
            {
                writer.WriteString("name", values["name"]);
                if (values.TryGetValue("episode", out var episode) && episode is not null)
                {
                    writer.WriteString("episode", episode);
                }
            }
            else
            {
                writer.WriteString("fileName", values["fileName"]);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string BuildCurl(string requestPath, string? jsonBody)
    {
        var builder = new StringBuilder("curl.exe");
        builder.Append(" -X ").Append(SelectedApi.Method.Method);
        builder.Append(" \"http://").Append(_context.Host).Append(':').Append(_context.Port).Append("/<TOKEN>").Append(RedactRequestPath(requestPath)).Append("\"");
        if (jsonBody is not null)
        {
            builder.Append(" -H \"Content-Type: application/json\"");
            builder.Append(" --data-raw ").Append(QuoteShell(RequestRecordRedactor.MaskJsonValues(jsonBody)));
        }
        return builder.ToString();
    }

    private static string RedactRequestPath(string requestPath)
    {
        var question = requestPath.IndexOf('?');
        if (question < 0)
        {
            return requestPath;
        }

        var path = requestPath[..question];
        var query = requestPath[(question + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var equals = part.IndexOf('=');
                var encodedName = equals < 0 ? part : part[..equals];
                return equals < 0 ? encodedName : $"{encodedName}=***";
            });
        return path + "?" + string.Join('&', query);
    }

    private static bool IsSensitiveJsonField(string name) =>
        CoreEnvDefinitionRules.IsSensitiveName(name) ||
        name.Equals("_m_h5_tk", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_m_h5_tk_enc", StringComparison.OrdinalIgnoreCase);

    private static void WriteRedactedJson(JsonElement element, Utf8JsonWriter writer, string? propertyName, string? token)
    {
        if (propertyName is not null && IsSensitiveJsonField(propertyName))
        {
            writer.WriteStringValue("***");
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedJson(property.Value, writer, property.Name, token);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedJson(item, writer, propertyName, token);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactKnownSecret(element.GetString() ?? string.Empty, token));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private void ReplaceRawResponse(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ClearRawResponse();
        _rawResponseBytes = source.ToArray();
    }

    private void ClearRawResponse()
    {
        if (_rawResponseBytes.Length > 0)
        {
            CryptographicOperations.ZeroMemory(_rawResponseBytes);
            _rawResponseBytes = [];
        }
    }

    private static string QuoteShell(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal) + "\"";

    private static string FormatResponse(string? bodyText, string contentType, string? token)
    {
        if (string.IsNullOrEmpty(bodyText))
        {
            return "<binary or empty response>";
        }

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var document = JsonDocument.Parse(bodyText);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    WriteRedactedJson(document.RootElement, writer, null, token);
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException)
            {
                return "<响应声明为 JSON，但正文无效；为避免泄露敏感值，未显示原文>";
            }
        }

        return RedactKnownSecret(bodyText, token);
    }

    private static string RedactKnownSecret(string value, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return value;
        }

        return value
            .Replace(token, "***", StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(token), "***", StringComparison.Ordinal);
    }

    public ValueTask DisposeAsync()
    {
        _requestCts.Cancel();
        _requestCts.Dispose();
        ClearRawResponse();
        return ValueTask.CompletedTask;
    }
}
