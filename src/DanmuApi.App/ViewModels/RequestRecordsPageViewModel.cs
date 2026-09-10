using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public enum RequestOutcomeFilter
{
    All,
    Success,
    Failure,
    Unknown,
}

public enum RequestRecordTimeRange
{
    All,
    LastHour,
    Today,
}

public sealed record RequestOutcomeFilterOption(RequestOutcomeFilter Value, string Title)
{
    public override string ToString() => Title;
}

public sealed record RequestTimeRangeOption(RequestRecordTimeRange Value, string Title)
{
    public override string ToString() => Title;
}

public sealed record RequestMethodFilterOption(string? Method, string Title)
{
    public override string ToString() => Title;
}

public sealed class RequestRecordListItem : ViewModelBase
{
    public RequestRecordListItem(
        string interfacePath,
        string method,
        DateTimeOffset? timestamp,
        string source,
        string parameters,
        int? statusCode,
        long? durationMilliseconds,
        bool? success,
        string errorMessage,
        string responseSummary,
        int? clientOrdinal = null)
    {
        Interface = interfacePath;
        Method = method;
        Timestamp = timestamp;
        Source = source;
        Parameters = parameters;
        StatusCode = statusCode;
        DurationMilliseconds = durationMilliseconds;
        Success = success;
        ErrorMessage = errorMessage;
        ResponseSummary = responseSummary;
        ClientOrdinal = clientOrdinal;
    }

    public string Interface { get; }
    public string Method { get; }
    public DateTimeOffset? Timestamp { get; }
    public string Source { get; }
    public string Parameters { get; }
    public int? StatusCode { get; }
    public long? DurationMilliseconds { get; }
    public bool? Success { get; }
    public string ErrorMessage { get; }
    public string ResponseSummary { get; }
    internal int? ClientOrdinal { get; }
    public bool HasParameters => Parameters.Length > 0;
    public bool HasError => ErrorMessage.Length > 0;
    public bool HasResponseSummary => ResponseSummary.Length > 0;
    public bool HasOutcome => Success is not null;
    public bool HasDetails => HasParameters || HasError || HasResponseSummary;
    public string TimestampDisplay => Timestamp is DateTimeOffset value
        ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        : "未知时间";
    public string OutcomeDisplay => Success switch
    {
        true => StatusCode is int status ? $"成功 · HTTP {status}" : "成功",
        false => StatusCode is int status ? $"失败 · HTTP {status}" : "失败",
        null => "结果未提供",
    };
    public string DurationDisplay => DurationMilliseconds is long duration
        ? $"{duration.ToString(CultureInfo.InvariantCulture)} ms"
        : "耗时未提供";
    public string ParametersDisplay => Parameters.Length == 0 ? string.Empty : FormatJson(Parameters);
    public string ResponseSummaryDisplay => ResponseSummary.Length == 0 ? string.Empty : FormatJson(ResponseSummary);

    internal bool MatchesQuery(string normalizedQuery)
    {
        if (normalizedQuery.Length == 0)
        {
            return true;
        }

        return string.Join(' ',
                Source,
                Method,
                Interface,
                StatusCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                ErrorMessage,
                Parameters,
                ResponseSummary)
            .Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    internal string Endpoint => Interface.Split('?', 2)[0].Trim() is { Length: > 0 } value
        ? value
        : "未知接口";

    private static string FormatJson(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                document.RootElement.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return value;
        }
    }
}

public sealed record RequestRecordInsights(
    int Total,
    int KnownOutcomeCount,
    int FailureRatePercent,
    long? P95DurationMilliseconds,
    int UniqueClients,
    int SlowCount,
    string? TopFailureEndpoint,
    int TopFailureCount);

public static class RequestRecordAnalysis
{
    public static IReadOnlyList<RequestRecordListItem> Filter(
        IReadOnlyList<RequestRecordListItem> records,
        string query,
        RequestOutcomeFilter outcome,
        string? method,
        RequestRecordTimeRange timeRange,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(records);
        var normalizedQuery = query.Trim();
        var today = now.ToLocalTime().Date;
        return records.Where(record =>
            record.MatchesQuery(normalizedQuery) &&
            (outcome == RequestOutcomeFilter.All ||
                outcome == RequestOutcomeFilter.Success && record.Success == true ||
                outcome == RequestOutcomeFilter.Failure && record.Success == false ||
                outcome == RequestOutcomeFilter.Unknown && record.Success is null) &&
            (method is null || record.Method.Equals(method, StringComparison.OrdinalIgnoreCase)) &&
            (timeRange == RequestRecordTimeRange.All ||
                record.Timestamp is DateTimeOffset timestamp &&
                (timeRange == RequestRecordTimeRange.LastHour && timestamp >= now.AddHours(-1) ||
                    timeRange == RequestRecordTimeRange.Today && timestamp.ToLocalTime().Date == today)))
            .ToArray();
    }

    public static RequestRecordInsights Summarize(IReadOnlyList<RequestRecordListItem> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var knownOutcomes = records.Where(record => record.Success is not null).ToArray();
        var failures = knownOutcomes.Where(record => record.Success == false).ToArray();
        var durations = records
            .Where(record => record.DurationMilliseconds is >= 0)
            .Select(record => record.DurationMilliseconds!.Value)
            .Order()
            .ToArray();
        long? p95 = durations.Length == 0
            ? null
            : durations[Math.Clamp((int)Math.Ceiling(durations.Length * 0.95) - 1, 0, durations.Length - 1)];
        var topFailure = failures
            .GroupBy(record => record.Endpoint, StringComparer.Ordinal)
            .Select(group => new { Endpoint = group.Key, Count = group.Count() })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Endpoint, StringComparer.Ordinal)
            .FirstOrDefault();

        return new RequestRecordInsights(
            records.Count,
            knownOutcomes.Length,
            knownOutcomes.Length == 0 ? 0 : (int)(failures.Length * 100d / knownOutcomes.Length),
            p95,
            records.Where(record => record.ClientOrdinal is not null).Select(record => record.ClientOrdinal).Distinct().Count(),
            records.Count(record => record.DurationMilliseconds is >= 2_000),
            topFailure?.Endpoint,
            topFailure?.Count ?? 0);
    }
}

public sealed partial class RequestRecordsPageViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaximumRecords = 200;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(5);

    private readonly AppPaths _paths;
    private readonly ICoreRequestRecordsClient _client;
    private readonly ILocalRequestRecordStore _localRecords;
    private readonly IRuntimeController _runtimeController;
    private readonly IUiDialogService _dialogs;
    private readonly IAppDiagnostics _diagnostics;
    private readonly SynchronizationContext? _uiContext;
    private readonly CancellationTokenSource _pollCts = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private IReadOnlyList<CoreRequestRecord> _remoteRecords = [];
    private IReadOnlyList<RequestRecordListItem> _allRecords = [];
    private bool _disposed;
    private bool _started;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRecord))]
    private RequestRecordListItem? _selectedRecord;

    public bool HasSelectedRecord => SelectedRecord is not null;

    [ObservableProperty]
    private int _todayRequestCount;

    [ObservableProperty]
    private DateTimeOffset? _lastUpdatedAt;

    [ObservableProperty]
    private string _diagnostic = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private RequestOutcomeFilterOption _selectedOutcomeFilter = null!;

    [ObservableProperty]
    private RequestMethodFilterOption _selectedMethodFilter = null!;

    [ObservableProperty]
    private RequestTimeRangeOption _selectedTimeRange = null!;

    [ObservableProperty]
    private RequestRecordInsights _insights = new(0, 0, 0, null, 0, 0, null, 0);

    public RequestRecordsPageViewModel(
        AppPaths paths,
        ICoreRequestRecordsClient client,
        ILocalRequestRecordStore localRecords,
        IRuntimeController runtimeController,
        IUiDialogService dialogs,
        IAppDiagnostics diagnostics)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _localRecords = localRecords ?? throw new ArgumentNullException(nameof(localRecords));
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _uiContext = SynchronizationContext.Current;
        OutcomeFilters =
        [
            new(RequestOutcomeFilter.All, "全部结果"),
            new(RequestOutcomeFilter.Success, "成功"),
            new(RequestOutcomeFilter.Failure, "失败"),
            new(RequestOutcomeFilter.Unknown, "结果未提供"),
        ];
        TimeRanges =
        [
            new(RequestRecordTimeRange.All, "全部时间"),
            new(RequestRecordTimeRange.LastHour, "近 1 小时"),
            new(RequestRecordTimeRange.Today, "今天"),
        ];
        _selectedOutcomeFilter = OutcomeFilters[0];
        _selectedTimeRange = TimeRanges[0];
        MethodFilters.Add(new(null, "全部方法"));
        _selectedMethodFilter = MethodFilters[0];
    }

    public ObservableCollection<RequestRecordListItem> Records { get; } = [];
    public ObservableCollection<RequestMethodFilterOption> MethodFilters { get; } = [];
    public IReadOnlyList<RequestOutcomeFilterOption> OutcomeFilters { get; }
    public IReadOnlyList<RequestTimeRangeOption> TimeRanges { get; }
    public bool HasRecords => Records.Count > 0;
    public bool HasAllRecords => _allRecords.Count > 0;
    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
    public string TodayRequestCountText => $"核心今日请求 {TodayRequestCount} 次";
    public string SummaryText => HasAllRecords
        ? $"显示 {Records.Count} / {_allRecords.Count} 条本地与核心请求"
        : "暂无请求记录";
    public string EmptyStateText => HasAllRecords ? "没有匹配的记录" : "暂无请求记录";
    public string LastUpdatedText => LastUpdatedAt is null
        ? "尚未读取"
        : $"最后更新 {LastUpdatedAt.Value.ToLocalTime():HH:mm:ss}";
    public string FailureRateText => Insights.KnownOutcomeCount == 0 ? "未提供" : $"{Insights.FailureRatePercent}%";
    public string P95Text => Insights.P95DurationMilliseconds is long duration ? $"{duration} ms" : "未提供";
    public string TopFailureText => Insights.TopFailureEndpoint is null
        ? (Insights.SlowCount > 0 ? $"慢请求 {Insights.SlowCount} 条" : string.Empty)
        : $"失败最多：{Insights.TopFailureEndpoint} ({Insights.TopFailureCount})" +
            (Insights.SlowCount > 0 ? $" · 慢请求 {Insights.SlowCount} 条" : string.Empty);
    public bool HasInsightWarning => Insights.TopFailureEndpoint is not null || Insights.SlowCount > 0;

    partial void OnSearchQueryChanged(string value) => ApplyFilters();
    partial void OnSelectedOutcomeFilterChanged(RequestOutcomeFilterOption value) => ApplyFilters();
    partial void OnSelectedMethodFilterChanged(RequestMethodFilterOption value) => ApplyFilters();
    partial void OnSelectedTimeRangeChanged(RequestTimeRangeOption value) => ApplyFilters();
    partial void OnInsightsChanged(RequestRecordInsights value)
    {
        OnPropertyChanged(nameof(FailureRateText));
        OnPropertyChanged(nameof(P95Text));
        OnPropertyChanged(nameof(TopFailureText));
        OnPropertyChanged(nameof(HasInsightWarning));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        _ = PollOnceSafelyAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => PollOnceSafelyAsync();

    [RelayCommand]
    private void Clear()
    {
        _localRecords.Clear();
        _remoteRecords = [];
        TodayRequestCount = 0;
        ReplaceRecords([]);
        SetDiagnostic(string.Empty);
        LastUpdatedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(TodayRequestCountText));
        OnPropertyChanged(nameof(LastUpdatedText));
    }

    [RelayCommand]
    private async Task CopyUrlAsync(RequestRecordListItem record)
    {
        ArgumentNullException.ThrowIfNull(record);
        try
        {
            await _dialogs.CopyTextAsync(record.Interface).ConfigureAwait(true);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
        {
            SetDiagnostic($"复制脱敏请求地址失败：{Describe(error)}");
            _diagnostics.Record("复制脱敏请求地址失败", error);
        }
    }

    private async Task PollOnceSafelyAsync()
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        await RunOnUiAsync(() => IsBusy = true).ConfigureAwait(false);
        try
        {
            await PollOnceAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_pollCts.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            var diagnostic = $"请求记录刷新失败：{Describe(error)}";
            try
            {
                await RunOnUiAsync(() =>
                {
                    ReplaceRecords(MergeRecords(_remoteRecords, _localRecords.Snapshot()));
                    SetDiagnostic(diagnostic);
                }).ConfigureAwait(false);
            }
            catch (Exception uiError)
            {
                _diagnostics.Record(
                    $"请求记录刷新失败且 UI 诊断更新失败（{uiError.GetType().FullName}）堆栈：{uiError.StackTrace ?? "无堆栈"}");
            }

            _diagnostics.Record(
                $"请求记录刷新失败（{error.GetType().FullName}）堆栈：{error.StackTrace ?? "无堆栈"}");
        }
        finally
        {
            try
            {
                await RunOnUiAsync(() => IsBusy = false).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _diagnostics.Record("请求记录忙碌状态复位失败", error);
            }
        }
    }

    private async Task PollOnceAsync()
    {
        await _pollGate.WaitAsync(_pollCts.Token).ConfigureAwait(false);
        try
        {
            var runtime = _runtimeController.Snapshot;
            if (runtime.State != DesktopRuntimeState.Running || runtime.Port is null)
            {
                await RunOnUiAsync(() =>
                {
                    ReplaceRecords(MergeRecords(_remoteRecords, _localRecords.Snapshot()));
                    SetDiagnostic("服务未运行；当前只显示本会话已记录的本地请求。");
                    LastUpdatedAt = DateTimeOffset.Now;
                    OnPropertyChanged(nameof(LastUpdatedText));
                }).ConfigureAwait(false);
                return;
            }

            var token = RuntimeTokenResolver.Resolve(
                Path.Combine(_paths.NodeProjectDirectory, "config", ".env"));
            var result = await _client.ReadAsync(
                "127.0.0.1",
                runtime.Port.Value,
                token,
                _pollCts.Token).ConfigureAwait(false);

            await RunOnUiAsync(() =>
            {
                if (!result.Succeeded)
                {
                    ReplaceRecords(MergeRecords(_remoteRecords, _localRecords.Snapshot()));
                    SetDiagnostic(result.Diagnostic);
                    _diagnostics.Record(result.Diagnostic);
                    return;
                }

                _remoteRecords = result.Records;
                ReplaceRecords(MergeRecords(_remoteRecords, _localRecords.Snapshot()));
                TodayRequestCount = result.TodayRequestCount;
                LastUpdatedAt = DateTimeOffset.Now;
                SetDiagnostic(string.Empty);
                OnPropertyChanged(nameof(TodayRequestCountText));
                OnPropertyChanged(nameof(LastUpdatedText));
            }).ConfigureAwait(false);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    internal static IReadOnlyList<RequestRecordListItem> MergeRecords(
        IReadOnlyList<CoreRequestRecord> remoteRecords,
        IReadOnlyList<LocalRequestRecord> localRecords)
    {
        var localItems = localRecords.Select(ToListItem).ToArray();
        var clientOrdinals = remoteRecords
            .Where(record => !string.IsNullOrWhiteSpace(record.ClientIp))
            .Select(record => record.ClientIp!)
            .Distinct(StringComparer.Ordinal)
            .Select((clientIp, index) => new { ClientIp = clientIp, Ordinal = index })
            .ToDictionary(item => item.ClientIp, item => item.Ordinal, StringComparer.Ordinal);
        var remoteItems = remoteRecords
            .Select(record => ToListItem(
                record,
                string.IsNullOrWhiteSpace(record.ClientIp) ? null : clientOrdinals[record.ClientIp!]))
            .Where(remote => !localItems.Any(local => IsDuplicate(local, remote)));
        return localItems
            .Concat(remoteItems)
            .OrderByDescending(item => item.Timestamp ?? DateTimeOffset.MinValue)
            .Take(MaximumRecords)
            .ToArray();
    }

    private static RequestRecordListItem ToListItem(LocalRequestRecord record) => new(
        record.Interface,
        record.Method,
        record.Timestamp,
        record.Scene,
        record.Parameters,
        record.StatusCode,
        record.DurationMilliseconds,
        record.Success,
        record.ErrorMessage ?? string.Empty,
        record.ResponseSummary);

    private static RequestRecordListItem ToListItem(CoreRequestRecord record, int? clientOrdinal)
    {
        var timestamp = DateTimeOffset.TryParse(
            record.Timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
                ? parsed
                : null as DateTimeOffset?;
        return new RequestRecordListItem(
            RequestRecordRedactor.MaskInterface(record.Interface),
            record.Method.ToUpperInvariant(),
            timestamp,
            string.IsNullOrWhiteSpace(record.ClientIp) ? "核心请求" : $"核心请求 · {MaskClientIp(record.ClientIp)}",
            string.IsNullOrWhiteSpace(record.ParametersJson) || record.ParametersJson == "null"
                ? string.Empty
                : RequestRecordRedactor.MaskJsonValues(record.ParametersJson),
            null,
            null,
            null,
            string.Empty,
            string.Empty,
            clientOrdinal);
    }

    private static bool IsDuplicate(RequestRecordListItem local, RequestRecordListItem remote) =>
        local.Method.Equals(remote.Method, StringComparison.OrdinalIgnoreCase) &&
        local.Interface.Equals(remote.Interface, StringComparison.Ordinal) &&
        local.Timestamp is DateTimeOffset localTime &&
        remote.Timestamp is DateTimeOffset remoteTime &&
        (localTime - remoteTime).Duration() <= DuplicateWindow;

    private static string MaskClientIp(string value) =>
        new(value.Select(character => character is '.' or ':' ? character : '*').ToArray());

    private void ReplaceRecords(IReadOnlyList<RequestRecordListItem> records)
    {
        _allRecords = records;
        RefreshMethodFilters();
        ApplyFilters();
        OnPropertyChanged(nameof(HasAllRecords));
    }

    private void RefreshMethodFilters()
    {
        var selectedMethod = SelectedMethodFilter?.Method;
        var methods = _allRecords
            .Select(record => record.Method)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(method => method, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MethodFilters.Clear();
        MethodFilters.Add(new(null, "全部方法"));
        foreach (var method in methods)
        {
            MethodFilters.Add(new(method, method));
        }

        var next = MethodFilters.FirstOrDefault(option =>
            string.Equals(option.Method, selectedMethod, StringComparison.OrdinalIgnoreCase)) ?? MethodFilters[0];
        if (!ReferenceEquals(SelectedMethodFilter, next))
        {
            SelectedMethodFilter = next;
        }
    }

    private void ApplyFilters()
    {
        if (SelectedOutcomeFilter is null || SelectedMethodFilter is null || SelectedTimeRange is null)
        {
            return;
        }

        var records = RequestRecordAnalysis.Filter(
            _allRecords,
            SearchQuery,
            SelectedOutcomeFilter.Value,
            SelectedMethodFilter.Method,
            SelectedTimeRange.Value,
            DateTimeOffset.Now);
        var selected = SelectedRecord;
        Records.Clear();
        foreach (var record in records)
        {
            Records.Add(record);
        }

        SelectedRecord = selected is null ? null : records.FirstOrDefault(record =>
            record.Method == selected.Method && record.Interface == selected.Interface &&
            record.Timestamp == selected.Timestamp && record.Source == selected.Source);
        Insights = RequestRecordAnalysis.Summarize(records);
        OnPropertyChanged(nameof(HasRecords));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    private void SetDiagnostic(string value)
    {
        Diagnostic = value;
        OnPropertyChanged(nameof(HasDiagnostic));
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                action();
                completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }, null);
        return completion.Task;
    }

    private static string Describe(Exception error) =>
        string.IsNullOrWhiteSpace(error.Message)
            ? error.GetType().Name
            : error.Message.Replace('\r', ' ').Replace('\n', ' ');

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollCts.Cancel();
        await _pollGate.WaitAsync().ConfigureAwait(false);
        _pollGate.Release();
        _pollGate.Dispose();
        _pollCts.Dispose();
    }
}
