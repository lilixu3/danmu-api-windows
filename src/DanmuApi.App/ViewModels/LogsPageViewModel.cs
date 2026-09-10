using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public sealed record LogSourceOption(
    string Id,
    LogSourceKind Value,
    string Label,
    string? Path,
    string Description)
{
    public bool IsCoreApi => Value == LogSourceKind.CoreApi;
    public override string ToString() => Label;
}

public sealed record LogLevelFilterOption(LogLevel? Value, string Label, int Count)
{
    public override string ToString() => Count > 0 ? $"{Label} ({Count})" : Label;
}

public sealed partial class LogsPageViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaxEntries = 5000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly AppPaths _paths;
    private readonly LogFileTailReader _reader;
    private readonly ICoreLogClient _coreLogClient;
    private readonly IRuntimeController _runtimeController;
    private readonly IUiDialogService _dialogService;
    private readonly IAppDiagnostics _diagnostics;
    private readonly SynchronizationContext? _uiContext;
    private readonly List<LogEntry> _entries = [];
    private readonly Dictionary<string, (long Position, string? Remainder)> _positions = [];
    private readonly ObservableCollection<LogEntry> _filteredEntries = [];
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private IReadOnlyList<string> _coreSnapshot = [];
    private long _sequence;
    private Task? _pollLoop;
    private readonly CancellationTokenSource _pollCts = new();
    private string? _lastPollFailureDiagnostic;
    private bool _initialized;
    private bool _disposed;

    [ObservableProperty]
    private LogSourceOption _selectedSource = null!;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LogLevel? _selectedLevel;

    [ObservableProperty]
    private string? _selectedCategory;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _wordWrap;

    [ObservableProperty]
    private bool _caseSensitive;

    [ObservableProperty]
    private string _diagnostic = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _lastUpdatedAt;

    [ObservableProperty]
    private int _pendingNewCount;

    [ObservableProperty]
    private bool _isViewportAtLatest = true;

    public LogsPageViewModel(
        AppPaths paths,
        LogFileTailReader reader,
        ICoreLogClient coreLogClient,
        IRuntimeController runtimeController,
        IUiDialogService dialogService,
        IAppDiagnostics diagnostics)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _coreLogClient = coreLogClient ?? throw new ArgumentNullException(nameof(coreLogClient));
        _runtimeController = runtimeController ?? throw new ArgumentNullException(nameof(runtimeController));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _uiContext = SynchronizationContext.Current;

        var runtimeLogs = Path.Combine(paths.NodeProjectDirectory, "logs");
        Sources =
        [
            new("core-api", LogSourceKind.CoreApi, "核心日志", null, "核心 /api/logs 返回的业务日志，最多保留核心最近 1000 行。"),
            new("node-stdout", LogSourceKind.CoreOutput, "Node 输出", Path.Combine(runtimeLogs, "node-stdout.log"), "Node 进程标准输出。"),
            new("node-stderr", LogSourceKind.CoreError, "Node 错误", Path.Combine(runtimeLogs, "node-stderr.log"), "Node 进程标准错误。"),
            new("host-lifecycle", LogSourceKind.Host, "宿主日志", paths.LifecycleLogFile, "桌面宿主的生命周期与诊断日志。"),
            new("host-tray", LogSourceKind.Host, "托盘日志", paths.TrayLogFile, "单实例唤醒与托盘相关日志。"),
        ];
        _selectedSource = Sources[0];
        _initialized = true;
    }

    public IReadOnlyList<LogSourceOption> Sources { get; }
    public ObservableCollection<LogEntry> FilteredEntries => _filteredEntries;
    public event EventHandler? ScrollToEndRequested;

    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(Diagnostic);
    public bool CanCopyOrExport => FilteredEntries.Count > 0;
    public bool CanOpenLogDirectory => !string.IsNullOrWhiteSpace(SelectedSource.Path);
    public bool IsFilterActive => !string.IsNullOrWhiteSpace(SearchText) || SelectedLevel is not null || !string.IsNullOrWhiteSpace(SelectedCategory);
    public bool HasPendingNewLogs => PendingNewCount > 0;
    public int TotalCount => _entries.Count;
    public int FilteredCount => FilteredEntries.Count;
    public string MatchSummaryText => IsFilterActive
        ? $"匹配 {FilteredCount} / {TotalCount} 行"
        : $"共 {TotalCount} 行";
    public string CopyButtonText => IsFilterActive ? $"复制筛选结果 ({FilteredCount})" : $"复制全部 ({TotalCount})";
    public string ExportButtonText => IsFilterActive ? $"导出筛选结果 ({FilteredCount})" : $"导出全部 ({TotalCount})";
    public string NewLogsButtonText => $"新增 {PendingNewCount} 条 · 查看最新";
    public string CopyStatusHint => IsFilterActive
        ? "复制与导出只针对当前筛选后的行，不会包含被过滤掉的内容。"
        : "当前没有启用筛选，复制与导出包含全部已载入日志。";
    public string LastUpdatedText => LastUpdatedAt is null
        ? "尚未载入"
        : $"最后更新 {LastUpdatedAt.Value.ToLocalTime():HH:mm:ss}";
    public bool IsEmpty => FilteredEntries.Count == 0;
    public bool HasLogs => _entries.Count > 0;
    public string EmptyStateText => HasLogs
        ? "当前筛选条件下没有匹配的日志行。"
        : SelectedSource.IsCoreApi
            ? "暂无核心业务日志；服务运行后会从 /api/logs 自动读取。"
            : "当前日志文件还没有可显示的内容。";
    public IReadOnlyList<LogLevelFilterOption> LevelOptions { get; private set; } = [];
    public IReadOnlyList<LogCategoryCount> Categories { get; private set; } = [];

    partial void OnSearchTextChanged(string value) => ReapplyFilter();
    partial void OnSelectedLevelChanged(LogLevel? value) => ReapplyFilter();
    partial void OnSelectedCategoryChanged(string? value) => ReapplyFilter();
    partial void OnCaseSensitiveChanged(bool value) => ReapplyFilter();

    partial void OnSelectedSourceChanged(LogSourceOption value)
    {
        if (!_initialized)
        {
            return;
        }

        _entries.Clear();
        _sequence = 0;
        _coreSnapshot = [];
        PendingNewCount = 0;
        IsViewportAtLatest = true;
        ReapplyFilter();
        _ = PollOnceSafelyAsync();
        OnPropertyChanged(nameof(CanOpenLogDirectory));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    partial void OnPendingNewCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPendingNewLogs));
        OnPropertyChanged(nameof(NewLogsButtonText));
    }

    partial void OnAutoScrollChanged(bool value)
    {
        if (value && IsViewportAtLatest && FilteredEntries.Count > 0)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = PollOnceSafelyAsync();
        _pollLoop ??= PollLoopAsync(_pollCts.Token);
    }

    public void NotifyViewportAtLatest(bool isAtLatest)
    {
        IsViewportAtLatest = isAtLatest;
        if (isAtLatest)
        {
            PendingNewCount = 0;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => PollOnceSafelyAsync();

    [RelayCommand]
    private void ViewLatest()
    {
        PendingNewCount = 0;
        IsViewportAtLatest = true;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedLevel = null;
        SelectedCategory = null;
        CaseSensitive = false;
    }

    [RelayCommand]
    private void SelectLevel(LogLevel? level) =>
        SelectedLevel = SelectedLevel == level ? null : level;

    [RelayCommand]
    private void SelectCategory(string? category) =>
        SelectedCategory = string.Equals(SelectedCategory, category, StringComparison.OrdinalIgnoreCase)
            ? null
            : category;

    [RelayCommand(CanExecute = nameof(CanCopyOrExport))]
    private async Task CopyAsync()
    {
        if (FilteredEntries.Count == 0)
        {
            SetDiagnostic("当前没有可复制的日志行。");
            return;
        }

        var text = LogFilter.ToPlainText(FilteredEntries, includeSource: false);
        try
        {
            await _dialogService.CopyTextAsync(text).ConfigureAwait(true);
            SetDiagnostic(IsFilterActive
                ? $"已复制筛选结果 {FilteredEntries.Count} 行。"
                : $"已复制全部 {FilteredEntries.Count} 行。");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException)
        {
            SetDiagnostic($"复制日志失败：{error.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyOrExport))]
    private async Task ExportAsync()
    {
        if (FilteredEntries.Count == 0)
        {
            SetDiagnostic("当前没有可导出的日志行。");
            return;
        }

        var text = LogFilter.ToPlainText(FilteredEntries, includeSource: true);
        var suggestedName = $"danmu-api-{SelectedSource.Id}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log";
        try
        {
            var saved = await _dialogService.SaveTextFileAsync(suggestedName, text).ConfigureAwait(true);
            SetDiagnostic(saved is null
                ? "已取消导出。"
                : $"已导出 {FilteredEntries.Count} 行到 {saved}");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetDiagnostic($"导出日志失败：{error.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenLogDirectory))]
    private async Task OpenLogDirectoryAsync()
    {
        var directory = Path.GetDirectoryName(SelectedSource.Path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            SetDiagnostic("核心业务日志来自 /api/logs，没有对应的本地日志目录。");
            return;
        }

        try
        {
            await _dialogService.OpenDirectoryAsync(directory).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetDiagnostic($"打开日志目录失败：{error.Message}");
        }
    }

    private async Task PollOnceSafelyAsync()
    {
        try
        {
            await PollOnceAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_pollCts.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            // 单次轮询的任何异常都不允许逃出：UI 上给简短原因，宿主日志补完整类型与堆栈，
            // 否则跨线程类故障（如 "Call from invalid thread"）无法定位确切抛出位置。
            var brief = $"日志轮询失败：{Describe(error)}";
            try
            {
                await RunOnUiAsync(() => SetDiagnostic(brief)).ConfigureAwait(false);
            }
            catch (Exception uiError)
            {
                _diagnostics.Record(
                    $"日志轮询失败且 UI 诊断更新失败（{uiError.GetType().FullName}）堆栈：{uiError.StackTrace ?? "无堆栈"}");
            }

            if (!string.Equals(_lastPollFailureDiagnostic, brief, StringComparison.Ordinal))
            {
                _lastPollFailureDiagnostic = brief;
                _diagnostics.Record(
                    $"日志轮询失败（{error.GetType().FullName}）堆栈：{error.StackTrace ?? "无堆栈"}");
            }
        }
    }

    private async Task PollOnceAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _pollGate.WaitAsync(_pollCts.Token).ConfigureAwait(false);
        try
        {
            var source = SelectedSource;
            if (source.IsCoreApi)
            {
                await PollCoreApiAsync(source).ConfigureAwait(false);
            }
            else
            {
                await PollFileAsync(source).ConfigureAwait(false);
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task PollCoreApiAsync(LogSourceOption source)
    {
        var runtime = _runtimeController.Snapshot;
        if (runtime.State != DesktopRuntimeState.Running || runtime.Port is null)
        {
            await ApplyIfCurrentAsync(source, () =>
            {
                SetDiagnostic("服务未运行，核心日志接口暂不可用。");
                NotifySummaryChanged();
            }).ConfigureAwait(false);
            return;
        }

        string token;
        try
        {
            token = RuntimeTokenResolver.Resolve(
                Path.Combine(_paths.NodeProjectDirectory, "config", ".env"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
        {
            await ApplyIfCurrentAsync(source, () =>
                SetDiagnostic($"读取核心日志访问 Token 失败：{Describe(error)}")).ConfigureAwait(false);
            return;
        }

        var result = await _coreLogClient.ReadAsync(
            "127.0.0.1",
            runtime.Port.Value,
            token,
            _pollCts.Token).ConfigureAwait(false);
        await ApplyIfCurrentAsync(source, () =>
        {
            if (!result.Succeeded)
            {
                SetDiagnostic(result.Diagnostic);
                NotifySummaryChanged();
                return;
            }

            ApplyCoreSnapshot(result.Lines);
            SetDiagnostic(string.Empty);
            LastUpdatedAt = DateTimeOffset.Now;
            NotifySummaryChanged();
        }).ConfigureAwait(false);
    }

    private async Task PollFileAsync(LogSourceOption source)
    {
        if (string.IsNullOrWhiteSpace(source.Path))
        {
            await ApplyIfCurrentAsync(source, () => SetDiagnostic("当前日志源缺少文件路径。")).ConfigureAwait(false);
            return;
        }

        _positions.TryGetValue(source.Id, out var state);
        var result = await _reader
            .ReadAsync(source.Path, state.Position, state.Remainder, _pollCts.Token)
            .ConfigureAwait(false);
        await ApplyIfCurrentAsync(source, () =>
        {
            if (!result.FileExists)
            {
                SetDiagnostic(result.Diagnostic ?? $"日志文件不存在：{source.Path}");
                _positions[source.Id] = (0, null);
                NotifySummaryChanged();
                return;
            }

            if (result.Restarted)
            {
                _entries.Clear();
                _sequence = 0;
            }

            _positions[source.Id] = (result.Position, result.PendingRemainder);
            if (result.Lines.Count > 0)
            {
                AppendLines(source.Value, result.Lines);
            }

            SetDiagnostic(result.Diagnostic ?? string.Empty);
            LastUpdatedAt = DateTimeOffset.Now;
            NotifySummaryChanged();
        }).ConfigureAwait(false);
    }

    private Task ApplyIfCurrentAsync(LogSourceOption source, Action action) =>
        RunOnUiAsync(() =>
        {
            if (!_disposed && ReferenceEquals(SelectedSource, source))
            {
                action();
            }
        });

    private void ApplyCoreSnapshot(IReadOnlyList<string> lines)
    {
        if (_coreSnapshot.SequenceEqual(lines, StringComparer.Ordinal))
        {
            return;
        }

        var overlap = FindSnapshotOverlap(_coreSnapshot, lines);
        if (_coreSnapshot.Count > 0 && overlap == 0)
        {
            _entries.Clear();
            _sequence = 0;
            ReapplyFilter();
        }

        var appended = lines.Skip(overlap).ToArray();
        _coreSnapshot = lines.ToArray();
        if (lines.Count == 0)
        {
            _entries.Clear();
            _sequence = 0;
            PendingNewCount = 0;
            ReapplyFilter();
        }
        else if (appended.Length > 0)
        {
            AppendLines(LogSourceKind.CoreApi, appended);
        }
    }

    private static int FindSnapshotOverlap(IReadOnlyList<string> previous, IReadOnlyList<string> current)
    {
        var maximum = Math.Min(previous.Count, current.Count);
        for (var length = maximum; length > 0; length--)
        {
            var previousStart = previous.Count - length;
            var matches = true;
            for (var index = 0; index < length; index++)
            {
                if (!string.Equals(previous[previousStart + index], current[index], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return length;
            }
        }

        return 0;
    }

    private void AppendLines(LogSourceKind source, IReadOnlyList<string> lines)
    {
        var lastCategory = _entries.Count > 0 ? _entries[^1].Category : null;
        var lastLevel = _entries.Count > 0 ? _entries[^1].Level : LogLevel.Info;
        var appended = new List<LogEntry>(lines.Count);

        foreach (var line in lines)
        {
            var parsed = LogLineParser.Parse(line, source);
            var level = parsed.Level ?? lastLevel;
            var category = parsed.Category ?? lastCategory ?? "system";
            var entry = new LogEntry(
                ++_sequence,
                source,
                parsed.Timestamp,
                level,
                parsed.Message,
                category,
                line)
            {
                IsContinuation = parsed.IsContinuation,
            };
            _entries.Add(entry);
            appended.Add(entry);
            lastCategory = category;
            lastLevel = level;
        }

        var trimmed = _entries.Count > MaxEntries;
        if (trimmed)
        {
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
            ReapplyFilter();
        }
        else
        {
            var query = CreateQuery();
            foreach (var entry in appended.Where(query.Matches))
            {
                FilteredEntries.Add(entry);
            }

            UpdateFilterMetadata();
        }

        if (AutoScroll && IsViewportAtLatest)
        {
            PendingNewCount = 0;
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            PendingNewCount += lines.Count;
        }
    }

    private LogQuery CreateQuery() => new(
        SearchText,
        SelectedLevel is { } level ? new HashSet<LogLevel> { level } : null,
        SelectedCategory,
        CaseSensitive);

    private void ReapplyFilter()
    {
        var filtered = LogFilter.Apply(_entries, CreateQuery());
        FilteredEntries.Clear();
        foreach (var entry in filtered)
        {
            FilteredEntries.Add(entry);
        }

        UpdateFilterMetadata();
    }

    private void UpdateFilterMetadata()
    {
        Categories = LogFilter.CountCategories(_entries);
        var levelCounts = LogFilter.CountLevels(_entries);
        LevelOptions =
        [
            new(null, "全部", _entries.Count),
            new(LogLevel.Error, "错误", levelCounts[LogLevel.Error]),
            new(LogLevel.Warn, "警告", levelCounts[LogLevel.Warn]),
            new(LogLevel.Info, "信息", levelCounts[LogLevel.Info]),
            new(LogLevel.Success, "成功", levelCounts[LogLevel.Success]),
            new(LogLevel.Debug, "调试", levelCounts[LogLevel.Debug]),
            new(LogLevel.Other, "其他", levelCounts[LogLevel.Other]),
        ];
        NotifySummaryChanged();
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(MatchSummaryText));
        OnPropertyChanged(nameof(CopyButtonText));
        OnPropertyChanged(nameof(ExportButtonText));
        OnPropertyChanged(nameof(CopyStatusHint));
        OnPropertyChanged(nameof(IsFilterActive));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasLogs));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(LastUpdatedText));
        OnPropertyChanged(nameof(LevelOptions));
        OnPropertyChanged(nameof(Categories));
        CopyCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        OpenLogDirectoryCommand.NotifyCanExecuteChanged();
    }

    private void SetDiagnostic(string message)
    {
        if (string.Equals(Diagnostic, message, StringComparison.Ordinal))
        {
            return;
        }

        Diagnostic = message;
        OnPropertyChanged(nameof(HasDiagnostic));
        if (!string.IsNullOrWhiteSpace(message) &&
            (message.Contains("失败", StringComparison.Ordinal) ||
             message.Contains("超时", StringComparison.Ordinal) ||
             message.Contains("HTTP ", StringComparison.Ordinal)))
        {
            _diagnostics.Record(message);
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await PollOnceSafelyAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // 对齐移动端 runCatching 语义：一次失败的轮询绝不允许终止轮询循环，
                // 否则核心日志会永久静默停止且没有任何报错。
                _diagnostics.Record(
                    $"日志轮询循环异常（{error.GetType().FullName}）堆栈：{error.StackTrace ?? "无堆栈"}");
            }
        }
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
        if (_pollLoop is not null)
        {
            try
            {
                await _pollLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _pollCts.Dispose();
        _pollGate.Dispose();
        ScrollToEndRequested = null;
    }
}
