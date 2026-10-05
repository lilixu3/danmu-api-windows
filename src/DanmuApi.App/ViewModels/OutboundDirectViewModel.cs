using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Platform;

namespace DanmuApi.App.ViewModels;

public sealed record OutboundHttpOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Only presents app-owned transport state. Enabling never starts or stops the core.</summary>
public sealed partial class OutboundDirectViewModel : ViewModelBase, IDisposable
{
    private readonly IOutboundDirectService _service;
    private readonly IAppDiagnostics _diagnostics;
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _monitoring;
    private CancellationTokenSource? _operation;
    private OutboundSettings? _savedSettings;
    private OutboundSettings? _recentTestSettings;
    private string _recentTestResult = string.Empty;
    private long _serviceChangeVersion;
    private bool _loading;
    private bool _disposed;
    private bool _statusReadFailed;
    private bool _isSaving;

    private enum SavePurpose { Configuration, Enabled, Source }

    [ObservableProperty] private OutboundDirectSnapshot _snapshot;
    [ObservableProperty] private bool _isConfigurationLoaded;
    [ObservableProperty] private bool _isInitialStatusPending = true;
    [ObservableProperty] private bool _isConfigurationEditing;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _bahamut;
    [ObservableProperty] private bool _tmdb;
    [ObservableProperty] private bool _dandan;
    [ObservableProperty] private bool _animeko;
    [ObservableProperty] private OutboundHttpOption? _selectedHttpOption;
    [ObservableProperty] private string _dohUrl = string.Empty;
    [ObservableProperty] private string _connectTimeoutText = string.Empty;
    [ObservableProperty] private bool _hasUnsavedChanges;
    [ObservableProperty] private string _validationText = string.Empty;
    [ObservableProperty] private string _diagnostic = string.Empty;
    [ObservableProperty] private string _diagnosticError = string.Empty;
    [ObservableProperty] private string _configurationMessage = string.Empty;
    [ObservableProperty] private string _diagnosticStatus = "尚未测速。测速逐个检查实际域名，连接正常不代表业务请求成功。";
    [ObservableProperty] private bool _lastSaveSucceeded;
    [ObservableProperty] private bool _hasRecentTest;
    [ObservableProperty] private string _recentTestSummary = string.Empty;
    [ObservableProperty] private string _recentTestTime = string.Empty;
    [ObservableProperty] private string _recentNormalCount = "—";
    [ObservableProperty] private string _recentNeedsKeyCount = "—";
    [ObservableProperty] private string _recentFailedCount = "—";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isDiagnosing;
    [ObservableProperty] private OutboundDiagnosticItem? _selectedDiagnostic;

    public OutboundDirectViewModel(IOutboundDirectService service, IAppDiagnostics diagnostics, TimeProvider? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _clock = clock ?? TimeProvider.System;
        _snapshot = service.Snapshot;
        // An initial off snapshot is also the service's unobserved state; saved Enabled is already known.
        _isInitialStatusPending = !_snapshot.ServiceRunning && !_snapshot.Applied && _snapshot.Status == "off";
        TryReadConfiguration(preserveDraft: false);
        _service.Changed += OnServiceChanged;
    }

    public IReadOnlyList<OutboundHttpOption> HttpOptions { get; } =
    [new("auto", "自动选择"), new("h2", "仅 HTTP/2"), new("h3", "仅 HTTP/3")];
    public ObservableCollection<OutboundDiagnosticItem> DiagnosticRows { get; } = [];

    public bool HasDiagnostic => Diagnostic.Length != 0;
    public bool HasDiagnosticError => DiagnosticError.Length != 0;
    public bool HasProblems => ValidationText.Length != 0;
    public bool HasDiagnosticRows => DiagnosticRows.Count != 0;
    public bool HasSelectedDiagnostic => SelectedDiagnostic is not null;
    public bool CanEditConfiguration => IsConfigurationLoaded && !IsBusy && !_disposed;
    public bool CanApply => CanEditConfiguration && !IsRefreshing && HasUnsavedChanges && !HasProblems;
    public bool CanRefresh => !IsBusy && !IsRefreshing && !_disposed;
    public bool CanToggleEnabled => CanEditConfiguration && !IsRefreshing;
    public bool CanToggleSource => CanEditConfiguration && !IsRefreshing && !IsConfigurationEditing;
    public bool SavedBahamutSelected => _savedSettings?.Sources.Contains("bahamut", StringComparer.Ordinal) == true;
    public bool SavedTmdbSelected => _savedSettings?.Sources.Contains("tmdb", StringComparer.Ordinal) == true;
    public bool SavedDandanSelected => _savedSettings?.Sources.Contains("dandan", StringComparer.Ordinal) == true;
    public bool SavedAnimekoSelected => _savedSettings?.Sources.Contains("animeko", StringComparer.Ordinal) == true;
    public string SourceSelectionText => IsConfigurationLoaded && _savedSettings is not null
        ? $"已选 {_savedSettings.Sources.Count.ToString(CultureInfo.InvariantCulture)} / 4" : "已选 — / 4";
    public bool CanOpenConfiguration => CanRefresh && !IsConfigurationEditing;
    public bool CanOpenDiagnostics => CanRefresh && !IsConfigurationEditing;
    public bool CanCancel => IsDiagnosing && _operation is { IsCancellationRequested: false };
    public bool IsEngineReady => !IsInitialStatusPending && !IsEngineFailed && _savedSettings?.Enabled == true &&
        Snapshot.Status == "ready" && Snapshot.ServiceRunning && Snapshot.Applied &&
        Snapshot.Settings is { } actual && OutboundSettings.Equivalent(actual, _savedSettings);
    public bool IsEngineFailed => _statusReadFailed || !IsConfigurationLoaded || HasDiagnostic ||
        Snapshot.Status == "failed" || Snapshot.Status is not ("off" or "starting" or "ready" or "failed");
    public bool CanDiagnose => CanRefresh && IsConfigurationLoaded && _savedSettings?.Enabled == true &&
        IsEngineReady && !HasUnsavedChanges;

    public string StatusText => IsEngineFailed ? "应用失败"
        : _isSaving ? "正在应用"
        : _savedSettings?.Enabled == false ? "已关闭"
        : IsInitialStatusPending ? "已开启待检查"
        : !Snapshot.ServiceRunning ? "已开启待服务"
        : IsEngineReady ? "增强直连运行中" : "正在应用";
    public string StatusDescription => !IsConfigurationLoaded
        ? "配置读取失败；请修复配置后刷新，不会用默认值覆盖现有文件。"
        : _statusReadFailed ? "当前服务状态读取失败；请查看原因后刷新。"
        : HasDiagnostic ? "增强直连操作失败；请查看原因并刷新实际保存与运行状态。"
        : IsInitialStatusPending ? "正在读取当前服务状态。"
        : _isSaving ? "正在保存增强直连配置；保存完成后仍需确认当前服务的应用状态。"
        : _savedSettings?.Enabled == false && !IsEngineFailed ? "增强直连已关闭，核心使用原有连接方式。"
        : Snapshot.ServiceRunning && !Snapshot.Applied
            ? "当前实例尚未应用增强配置。旧宿主需从概览页手动重启核心后再刷新状态。"
        : IsEngineFailed ? "当前服务未通过增强直连验证；请查看原因后刷新。"
        : _savedSettings?.Enabled == false ? "增强直连已关闭，核心使用原有连接方式。"
        : !Snapshot.ServiceRunning ? "增强直连已开启；请从概览页启动弹幕服务。"
        : IsEngineReady ? "增强直连已通过当前服务与辅助进程验证；各域名连接和业务认证请通过测速确认。"
        : "正在应用已保存的增强配置，请稍候并刷新确认。";
    public string StatusReason => HasDiagnostic ? Diagnostic
        : IsInitialStatusPending ? string.Empty
        : Snapshot.Status is not ("off" or "starting" or "ready" or "failed")
            ? "当前服务返回了不支持的增强直连状态，拒绝报告运行成功。"
            : SafeText(Snapshot.Reason);
    public string SourceSummary => !IsConfigurationLoaded || _savedSettings is null ? "配置读取失败"
        : _savedSettings.Sources.Count == 0 ? "未选择来源"
        : string.Join(" / ", _savedSettings.Sources.Select(SourceLabel));
    public string ConnectionSummary
    {
        get
        {
            if (!IsConfigurationLoaded || _savedSettings is null) return "配置读取失败";
            var http = HttpLabel(_savedSettings.HttpVersion);
            var doh = _savedSettings.DohUrl.Length == 0 ? "内置 DoH" : $"DoH：{new Uri(_savedSettings.DohUrl).Host}";
            return $"{http} · {doh} · 连接超时 {_savedSettings.ConnectTimeoutMs.ToString(CultureInfo.InvariantCulture)} ms";
        }
    }
    public string CapabilityText => !Snapshot.ServiceRunning ? string.Empty : string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(Snapshot.HelperVersion) ? null : $"辅助程序 {SafeText(Snapshot.HelperVersion)}",
        Snapshot.ProtocolVersion is > 0 ? $"能力协议 {Snapshot.ProtocolVersion.Value.ToString(CultureInfo.InvariantCulture)}" : null,
    }.Where(value => value is not null));
    public string ProcessText => !Snapshot.ServiceRunning ? string.Empty : string.Join(" · ", new[]
    {
        Snapshot.NodePid is > 0 ? $"Node PID {Snapshot.NodePid.Value.ToString(CultureInfo.InvariantCulture)}" : null,
        Snapshot.HelperPid is > 0 ? $"辅助进程 PID {Snapshot.HelperPid.Value.ToString(CultureInfo.InvariantCulture)}" : null,
    }.Where(value => value is not null));
    public bool HasRuntimeDetails => CapabilityText.Length != 0 || ProcessText.Length != 0;
    public string FormStateText => !IsConfigurationLoaded
        ? "配置读取失败；修复配置后刷新，不会用默认值覆盖现有文件。"
        : HasUnsavedChanges ? "有未保存的修改；保存后统一应用全部连接选项。" : "连接选项已保存。";
    public string DiagnoseActionText => HasDiagnosticRows ? "重新测速" : "开始测速";
    public string DiagnoseBlockedReason => IsBusy ? "操作进行中，可取消当前测速。"
        : IsRefreshing ? "正在读取当前服务状态，请稍候。"
        : !IsConfigurationLoaded ? "配置不可读取；请先刷新并处理读取错误。"
        : HasUnsavedChanges ? "请先保存或取消配置修改；测速使用已保存的来源与连接选项。"
        : _savedSettings?.Enabled != true ? "增强直连已关闭；可在主页面启用，测速不会自动开启。"
        : IsInitialStatusPending ? "正在读取当前服务状态。"
        : !Snapshot.ServiceRunning ? "核心服务未运行；请从概览页启动服务。"
        : !Snapshot.Applied ? "当前实例尚未应用增强；请从概览页手动重启核心。"
        : !IsEngineReady ? "增强直连尚未通过验证；请刷新状态并查看失败原因。" : string.Empty;

    /// <summary>Start an owned configuration dialog from a validated read of the actual saved file.</summary>
    public bool BeginConfigurationEdit()
    {
        if (!CanOpenConfiguration) return false;
        LastSaveSucceeded = false;
        ConfigurationMessage = string.Empty;
        if (!TryReadConfiguration(preserveDraft: false)) return false;
        IsConfigurationEditing = true;
        return true;
    }

    public void DiscardConfigurationEdit()
    {
        if (_disposed || IsBusy) return;
        if (_savedSettings is not null) LoadForm(_savedSettings);
        IsConfigurationEditing = false;
    }

    public void ReportDialogFailure(string operation, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!_disposed) SetFailure(operation, error);
        else _diagnostics.Record(FailureText(operation, error));
    }

    public Task ActivateAsync() => CanRefresh ? RefreshAsync() : Task.CompletedTask;
    public bool IsMonitoring => _monitoring is not null;
    public string MonitoringText => IsMonitoring ? "页面显示期间每 2 秒刷新实际状态。" : "页面监控已暂停；重新进入时刷新。";

    public void BeginMonitoring()
    {
        if (_disposed || _monitoring is not null) return;
        _monitoring = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        OnPropertyChanged(nameof(IsMonitoring));
        OnPropertyChanged(nameof(MonitoringText));
        _ = MonitorAsync(_monitoring);
    }

    public void EndMonitoring()
    {
        var monitoring = _monitoring;
        _monitoring = null;
        monitoring?.Cancel();
        OnPropertyChanged(nameof(IsMonitoring));
        OnPropertyChanged(nameof(MonitoringText));
    }

    private async Task MonitorAsync(CancellationTokenSource monitoring)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _clock);
            do
            {
                if (CanRefresh) await RefreshCoreAsync(monitoring.Token).ConfigureAwait(true);
            }
            while (await timer.WaitForNextTickAsync(monitoring.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException) when (monitoring.IsCancellationRequested)
        {
            _diagnostics.Record("增强直连页面监控已结束（页面离开或应用退出）。");
        }
        catch (Exception error)
        {
            Dispatch(() => SetFailure("增强直连页面监控已停止", error));
        }
        finally
        {
            if (ReferenceEquals(_monitoring, monitoring))
            {
                _monitoring = null;
                Dispatch(() => { OnPropertyChanged(nameof(IsMonitoring)); OnPropertyChanged(nameof(MonitoringText)); });
            }
            monitoring.Dispose();
        }
    }

    partial void OnSnapshotChanged(OutboundDirectSnapshot value) => NotifyState();
    partial void OnIsConfigurationLoadedChanged(bool value)
    {
        OnPropertyChanged(nameof(FormStateText));
        NotifyState();
    }
    partial void OnIsInitialStatusPendingChanged(bool value) => NotifyState();
    partial void OnIsConfigurationEditingChanged(bool value) => NotifyCommands();
    partial void OnDiagnosticChanged(string value) { OnPropertyChanged(nameof(HasDiagnostic)); NotifyState(); }
    partial void OnDiagnosticErrorChanged(string value) => OnPropertyChanged(nameof(HasDiagnosticError));
    partial void OnSelectedDiagnosticChanged(OutboundDiagnosticItem? value) => OnPropertyChanged(nameof(HasSelectedDiagnostic));
    partial void OnIsBusyChanged(bool value) => NotifyState();
    partial void OnIsRefreshingChanged(bool value) => NotifyCommands();
    partial void OnIsDiagnosingChanged(bool value) => NotifyCommands();
    partial void OnEnabledChanged(bool value) => NotifyCommands();
    partial void OnBahamutChanged(bool value) => FormChanged();
    partial void OnTmdbChanged(bool value) => FormChanged();
    partial void OnDandanChanged(bool value) => FormChanged();
    partial void OnAnimekoChanged(bool value) => FormChanged();
    partial void OnSelectedHttpOptionChanged(OutboundHttpOption? value) => FormChanged();
    partial void OnDohUrlChanged(string value) => FormChanged();
    partial void OnConnectTimeoutTextChanged(string value) => FormChanged();

    private bool TryReadConfiguration(bool preserveDraft)
    {
        try
        {
            var settings = _service.ReadSettings();
            OutboundSettings.Validate(settings);
            _savedSettings = settings with { Sources = settings.Sources.ToArray() };
            UpdateRecentTestSummary();
            IsConfigurationLoaded = true;
            Enabled = settings.Enabled;
            if (preserveDraft) FormChanged();
            else LoadForm(settings);
            NotifyState();
            return true;
        }
        catch (Exception error)
        {
            IsConfigurationLoaded = false;
            SetFailure("读取增强直连配置失败", error);
            return false;
        }
    }

    private void LoadForm(OutboundSettings settings)
    {
        _loading = true;
        try
        {
            Enabled = settings.Enabled;
            Bahamut = settings.Sources.Contains("bahamut");
            Tmdb = settings.Sources.Contains("tmdb");
            Dandan = settings.Sources.Contains("dandan");
            Animeko = settings.Sources.Contains("animeko");
            SelectedHttpOption = HttpOptions.Single(option => option.Value == settings.HttpVersion);
            DohUrl = settings.DohUrl;
            ConnectTimeoutText = settings.ConnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
        }
        finally { _loading = false; }
        ValidationText = string.Empty;
        HasUnsavedChanges = false;
        FormChanged();
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(FormStateText));
        NotifyCommands();
    }

    private OutboundSettings? BuildForm(out string problem)
    {
        if (_savedSettings is null)
        {
            problem = "尚未成功读取保存配置，拒绝生成默认配置。";
            return null;
        }
        List<string> problems = [];
        List<string> sources = [];
        if (Bahamut) sources.Add("bahamut");
        if (Tmdb) sources.Add("tmdb");
        if (Dandan) sources.Add("dandan");
        if (Animeko) sources.Add("animeko");
        if (_savedSettings.Enabled && sources.Count == 0) problems.Add("来源：启用时至少选择一个来源。");
        if (!int.TryParse(ConnectTimeoutText, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout) || timeout is < 1 or > 60000)
            problems.Add("连接超时：请输入 1 到 60000 的整数，单位毫秒（ms）。");
        if (SelectedHttpOption is null) problems.Add("HTTP 模式：请选择自动协商、HTTP/2 或 HTTP/3。");
        var candidate = new OutboundSettings(_savedSettings.Enabled, sources.ToArray(), SelectedHttpOption?.Value ?? string.Empty, DohUrl, timeout);
        // Reuse the platform contract for HTTPS URL and transport validation; never normalize a bad value.
        if (problems.Count == 0)
        {
            try { OutboundSettings.Validate(candidate); }
            catch (Exception error) { problems.Add(SafeText(error.Message)); }
        }
        problem = string.Join(Environment.NewLine, problems);
        return problems.Count == 0 ? candidate : null;
    }

    private void FormChanged()
    {
        if (_loading || !IsConfigurationLoaded) return;
        var form = BuildForm(out var problems);
        ValidationText = problems;
        HasUnsavedChanges = form is null || _savedSettings is null || !OutboundSettings.Equivalent(form, _savedSettings);
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(FormStateText));
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyConfigurationAsync()
    {
        LastSaveSucceeded = false;
        if (_disposed || IsBusy || IsRefreshing) return;
        if (!IsConfigurationLoaded) { SetFailure("配置读取失败，拒绝覆盖保存。", null); return; }
        var settings = BuildForm(out var problem);
        if (settings is null) { ValidationText = problem; OnPropertyChanged(nameof(HasProblems)); SetFailure("配置未保存：" + problem, null); return; }
        if (!HasUnsavedChanges) return;
        await SaveAsync(settings, SavePurpose.Configuration).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanToggleEnabled))]
    private async Task ToggleEnabledAsync()
    {
        LastSaveSucceeded = false;
        if (!CanToggleEnabled || _savedSettings is null) return;
        var candidate = _savedSettings with { Enabled = !_savedSettings.Enabled };
        if (candidate.Enabled && candidate.Sources.Count == 0)
        {
            ConfigurationMessage = "启用失败：请先在主页面勾选至少一个来源，再打开增强直连。";
            SetFailure(ConfigurationMessage, null);
            return;
        }
        await SaveAsync(candidate, SavePurpose.Enabled).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanToggleSource))]
    private async Task ToggleSourceAsync(string? source)
    {
        try
        {
            if (!CanToggleSource || _savedSettings is null) return;
            LastSaveSucceeded = false;
            if (source is not ("bahamut" or "tmdb" or "dandan" or "animeko"))
            {
                ConfigurationMessage = "来源修改失败：未知的增强直连来源。";
                SetFailure(ConfigurationMessage, null);
                return;
            }
            var sources = _savedSettings.Sources.Contains(source, StringComparer.Ordinal)
                ? _savedSettings.Sources.Where(value => value != source).ToArray()
                : _savedSettings.Sources.Append(source).ToArray();
            if (_savedSettings.Enabled && sources.Length == 0)
            {
                ConfigurationMessage = "来源修改失败：启用增强直连时至少选择一个来源。";
                SetFailure(ConfigurationMessage, null);
                return;
            }
            await SaveAsync(_savedSettings with { Sources = sources }, SavePurpose.Source).ConfigureAwait(true);
        }
        finally { NotifySavedSources(); }
    }

    private async Task SaveAsync(OutboundSettings settings, SavePurpose purpose)
    {
        var preserveDraft = HasUnsavedChanges || IsConfigurationEditing;
        var readbackAttempted = false;
        LastSaveSucceeded = false;
        _isSaving = true;
        IsBusy = true;
        Diagnostic = string.Empty;
        ConfigurationMessage = "正在保存并应用配置…";
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        try
        {
            var result = await _service.SaveAsync(settings, operation.Token).ConfigureAwait(true);
            if (_disposed) return;
            operation.Token.ThrowIfCancellationRequested();
            IsInitialStatusPending = false;
            ApplySnapshot(_service.Snapshot);
            ConfigurationMessage = SafeText(result.Diagnostic);
            if (!result.Succeeded) SetFailure("应用增强直连配置失败：" + result.Diagnostic, null);
            // A failed operation may still have written the file. Read actual settings without claiming application.
            OutboundSettings actual;
            readbackAttempted = true;
            try
            {
                actual = _service.ReadSettings();
                OutboundSettings.Validate(actual);
            }
            catch
            {
                IsConfigurationLoaded = false;
                throw; // The operation catch below records the sanitized read/validation failure.
            }
            _savedSettings = actual with { Sources = actual.Sources.ToArray() };
            UpdateRecentTestSummary();
            IsConfigurationLoaded = true;
            Enabled = actual.Enabled;
            if (purpose == SavePurpose.Source && !preserveDraft) LoadForm(actual);
            else FormChanged();
            NotifyState();
            if (result.Succeeded && !OutboundSettings.Equivalent(actual, settings))
                throw new IOException("增强直连配置保存回读不一致。");
            if (purpose == SavePurpose.Configuration && result.Succeeded)
            {
                LoadForm(actual);
                IsConfigurationEditing = false;
            }
            LastSaveSucceeded = result.Succeeded;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            ConfigurationMessage = "配置应用已取消；请刷新确认实际保存与运行状态。";
            if (!_disposed) SetFailure(ConfigurationMessage, null);
            else _diagnostics.Record(ConfigurationMessage);
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                ConfigurationMessage = "配置应用失败；请查看原因并刷新实际状态。";
                SetFailure("应用增强直连配置失败", error);
                if (purpose == SavePurpose.Source && !readbackAttempted && !TryReadConfiguration(preserveDraft))
                    SetFailure(FailureText("应用增强直连配置失败", error) + Environment.NewLine + Diagnostic, null);
            }
            else _diagnostics.Record(FailureText("增强直连配置应用结束时失败", error));
        }
        finally
        {
            _operation = null;
            _isSaving = false;
            if (!_disposed) { IsBusy = false; NotifyState(); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => _disposed ? Task.CompletedTask : RefreshCoreAsync(_lifetime.Token);

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!CanRefresh) return;
        IsRefreshing = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _operation = operation;
        try
        {
            var changeVersion = Volatile.Read(ref _serviceChangeVersion);
            var snapshot = await _service.RefreshAsync(operation.Token).ConfigureAwait(true);
            if (_disposed) return;
            operation.Token.ThrowIfCancellationRequested();
            _statusReadFailed = false;
            IsInitialStatusPending = false;
            if (changeVersion != Volatile.Read(ref _serviceChangeVersion)) snapshot = _service.Snapshot;
            ApplySnapshot(snapshot);
            snapshot = Snapshot;
            var configurationRead = TryReadConfiguration(preserveDraft: HasUnsavedChanges || IsConfigurationEditing);
            if (snapshot.Status == "failed") SetFailure(snapshot.Reason, null);
            else if (snapshot.Status is not ("off" or "starting" or "ready"))
                SetFailure("当前服务返回了不支持的增强直连状态，拒绝报告运行成功。", null);
            else if (configurationRead)
            {
                // Only successful status AND settings reads clear earlier host/configuration errors.
                Diagnostic = string.Empty;
                ConfigurationMessage = string.Empty;
            }
            NotifyState();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_disposed) SetFailure("状态刷新已取消；显示的仍是上次观测结果。", null);
            else _diagnostics.Record("增强直连状态刷新已取消。");
        }
        catch (Exception error)
        {
            _statusReadFailed = true;
            if (!_disposed) { IsInitialStatusPending = false; SetFailure("刷新增强直连状态失败", error); NotifyState(); }
            else _diagnostics.Record(FailureText("增强直连刷新结束时失败", error));
        }
        finally { _operation = null; if (!_disposed) IsRefreshing = false; }
    }

    [RelayCommand(CanExecute = nameof(CanDiagnose))]
    private async Task DiagnoseAsync()
    {
        if (!CanDiagnose) { DiagnosticStatus = DiagnoseBlockedReason; return; }
        IsBusy = true;
        IsDiagnosing = true;
        DiagnosticError = string.Empty;
        DiagnosticStatus = "正在测速；每个目标域名单独保留结果，现有记录仍为上次已完成测速。";
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        NotifyCommands();
        try
        {
            var run = await _service.DiagnoseAsync(operation.Token).ConfigureAwait(true);
            if (_disposed) return;
            ApplySnapshot(_service.Snapshot);
            TryReadConfiguration(preserveDraft: HasUnsavedChanges || IsConfigurationEditing);
            if (run.Rows.Count != 0 && run.Settings is not null)
            {
                DiagnosticRows.Clear();
                foreach (var row in run.Rows) DiagnosticRows.Add(new OutboundDiagnosticItem(row, run.Settings.HttpVersion));
                SelectedDiagnostic = DiagnosticRows.FirstOrDefault();
                NotifyRows();
            }
            if (run.Status != OutboundDiagnosticRunStatus.Completed)
            {
                DiagnosticStatus = run.Status == OutboundDiagnosticRunStatus.Cancelled
                    ? "测速已取消；保留上次已完成测速记录。" : "测速失败；保留上次已完成测速记录。";
                SetDiagnosticFailure(DiagnosticStatus + " " + run.Diagnostic, null);
                return;
            }
            if (run.Settings is null || run.CompletedAt is null || run.Rows.Count == 0)
                throw new InvalidOperationException("测速未返回完整的已验证配置、目标域名结果与完成时间，拒绝报告成功。");
            var rows = run.Rows;
            var normal = DiagnosticRows.Count(row => row.Succeeded);
            var needsKey = DiagnosticRows.Count(row => row.NeedsApiKey);
            var failed = rows.Count - normal - needsKey;
            var summary = $"{rows.Count} 个域名 · 连接正常 {normal} · 需要API密钥 {needsKey} · 连接失败 {failed}";
            DiagnosticStatus = "测速完成：" + summary + "。连接正常不代表全部业务请求成功。" +
                (normal != rows.Count ? "选中对应行查看连接或认证详情。" : string.Empty);
            RecentNormalCount = normal.ToString(CultureInfo.InvariantCulture);
            RecentNeedsKeyCount = needsKey.ToString(CultureInfo.InvariantCulture);
            RecentFailedCount = failed.ToString(CultureInfo.InvariantCulture);
            RememberTest(summary, run.Settings, run.CompletedAt.Value);
            // Domain outcomes belong to rows, not a test-wide error banner. Keep every safe diagnostic in the log.
            foreach (var row in DiagnosticRows.Where(row => !row.Succeeded))
                _diagnostics.Record(SafeText($"增强直连测速 {row.Source}/{row.Host}：{row.DetailText}"));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_disposed)
            {
                DiagnosticStatus = "测速已取消；未完成的域名没有成功结论。";
                SetDiagnosticFailure(DiagnosticStatus, null);
                // No completion record is created for an incomplete run.
            }
            else _diagnostics.Record("增强直连测速已取消。");
        }
        catch (Exception error)
        {
            if (!_disposed)
            {
                DiagnosticStatus = "测速失败；请查看具体原因后重新测速。";
                SetDiagnosticFailure("增强直连测速失败", error);
                // Preserve the prior completed configuration, counters and completion time.
            }
            else _diagnostics.Record(FailureText("增强直连测速结束时失败", error));
        }
        finally { _operation = null; if (!_disposed) { IsDiagnosing = false; IsBusy = false; } }
    }

    private void RememberTest(string summary, OutboundSettings settings, DateTimeOffset completedAt)
    {
        _recentTestResult = summary;
        _recentTestSettings = settings with { Sources = Array.AsReadOnly(settings.Sources.ToArray()) };
        UpdateRecentTestSummary();
        RecentTestTime = TimeZoneInfo.ConvertTime(completedAt, _clock.LocalTimeZone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        HasRecentTest = true;
    }

    private void UpdateRecentTestSummary()
    {
        if (_recentTestSettings is null) return;
        RecentTestSummary = _recentTestResult + (_savedSettings is not null && OutboundSettings.Equivalent(_recentTestSettings, _savedSettings)
            ? string.Empty : " · 上次测试，配置已更改");
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        var operation = _operation;
        if (_disposed || !IsDiagnosing || operation is null || operation.IsCancellationRequested) return;
        try
        {
            // Cancellation callbacks may finish DiagnoseAsync inline and publish its terminal status.
            DiagnosticStatus = "已请求取消，正在等待当前连接检查结束…";
            operation.Cancel();
            NotifyCommands();
        }
        catch (Exception error) { SetDiagnosticFailure("取消增强直连测速失败", error); }
    }

    private void OnServiceChanged(object? sender, OutboundDirectSnapshot snapshot)
    {
        var changeVersion = Interlocked.Increment(ref _serviceChangeVersion);
        Dispatch(() =>
        {
            // A queued ready must not run after a newer stop/restart notification.
            if (changeVersion != Volatile.Read(ref _serviceChangeVersion)) return;
            IsInitialStatusPending = false;
            ApplySnapshot(snapshot);
            NotifyState();
        });
    }

    private void ApplySnapshot(OutboundDirectSnapshot candidate)
    {
        var latest = _service.Snapshot;
        if (candidate.RuntimeEpoch < latest.RuntimeEpoch || candidate.Revision < latest.Revision) candidate = latest;
        if (candidate.RuntimeEpoch < Snapshot.RuntimeEpoch || candidate.Revision < Snapshot.Revision) return;
        Snapshot = candidate;
    }

    private void Dispatch(Action action)
    {
        if (_disposed) return;
        void Apply() { if (!_disposed) action(); }
        if (_uiContext is not null)
        {
            if (SynchronizationContext.Current == _uiContext) Apply();
            else _uiContext.Post(_ => Apply(), null);
        }
        else if (Application.Current is not null && !Dispatcher.UIThread.CheckAccess()) Dispatcher.UIThread.Post(Apply);
        else Apply();
    }

    private void SetFailure(string message, Exception? error)
    {
        Diagnostic = FailureText(message, error);
        _diagnostics.Record(Diagnostic);
    }

    private void SetDiagnosticFailure(string message, Exception? error)
    {
        DiagnosticError = FailureText(message, error);
        _diagnostics.Record(DiagnosticError);
    }

    private static string FailureText(string message, Exception? error)
    {
        List<string> details = [message];
        for (var current = error; current is not null && details.Count < 6; current = current.InnerException)
            details.Add($"{current.GetType().Name}；{current.Message}");
        return SafeText(string.Join("：", details));
    }

    private void NotifyRows()
    {
        OnPropertyChanged(nameof(HasDiagnosticRows));
        OnPropertyChanged(nameof(DiagnoseActionText));
    }
    private void NotifySavedSources()
    {
        OnPropertyChanged(nameof(SavedBahamutSelected));
        OnPropertyChanged(nameof(SavedTmdbSelected));
        OnPropertyChanged(nameof(SavedDandanSelected));
        OnPropertyChanged(nameof(SavedAnimekoSelected));
        OnPropertyChanged(nameof(SourceSelectionText));
    }
    private void NotifyState()
    {
        NotifySavedSources();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDescription));
        OnPropertyChanged(nameof(StatusReason));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(HasRuntimeDetails));
        OnPropertyChanged(nameof(CapabilityText));
        OnPropertyChanged(nameof(ProcessText));
        OnPropertyChanged(nameof(IsEngineReady));
        OnPropertyChanged(nameof(IsEngineFailed));
        NotifyCommands();
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanToggleEnabled));
        OnPropertyChanged(nameof(CanToggleSource));
        OnPropertyChanged(nameof(CanOpenConfiguration));
        OnPropertyChanged(nameof(CanOpenDiagnostics));
        OnPropertyChanged(nameof(CanDiagnose));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(DiagnoseBlockedReason));
        ApplyConfigurationCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        ToggleSourceCommand.NotifyCanExecuteChanged();
        DiagnoseCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    internal static string SourceLabel(string source) => source switch
    { "bahamut" => "巴哈姆特", "tmdb" => "TMDB", "dandan" => "弹弹play", "animeko" => "Animeko", _ => source };

    internal static string HttpLabel(string version) => version switch
    { "auto" => "自动协商", "h2" => "HTTP/2", "h3" => "HTTP/3", _ => "未确认" };

    internal static string SafeText(string text)
    {
        var safe = Regex.Replace(text, @"https?://[^\s<>""']+", match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/…" : "[地址已隐藏]", RegexOptions.IgnoreCase);
        safe = Regex.Replace(safe, @"[0-9a-fA-F]{64}", "[会话值已隐藏]");
        safe = Regex.Replace(safe, @"\bBearer\s+[^\s,;]+", "Bearer ***", RegexOptions.IgnoreCase);
        safe = Regex.Replace(safe, @"\b(token|cookie|password|passwd|secret|api[_-]?key|authorization)\b[""']?\s*[:=]\s*(""[^""]*""|'[^']*'|[^\s,;]+)", "$1=***", RegexOptions.IgnoreCase);
        return safe;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Changed -= OnServiceChanged;
        EndMonitoring();
        _lifetime.Cancel();
        _operation?.Cancel();
        _lifetime.Dispose();
        NotifyCommands();
    }
}

/// <summary>A row remains tied to its original domain; no source-wide fallback success aggregation.</summary>
public sealed class OutboundDiagnosticItem
{
    private readonly OutboundDiagnosticRow _row;
    private readonly string _httpVersion;
    private readonly string? _rawProtocol;
    public OutboundDiagnosticItem(OutboundDiagnosticRow row, string httpVersion)
    {
        _row = row;
        _httpVersion = httpVersion;
        _rawProtocol = row.Protocol;
    }
    public string Source => OutboundDirectViewModel.SourceLabel(_row.Source);
    public string Host => _row.Host;
    public string Protocol => OutboundDirectViewModel.HttpLabel(_rawProtocol ?? string.Empty);
    public string EchText => !_row.EchRequired
        ? _row.EchAccepted == true ? "策略不符 · 失败" : "不使用"
        : _row.EchAccepted == true ? "已接受" : _row.EchAccepted == false ? "未接受 · 失败" : "未确认 · 失败";
    public string HttpStatus => _row.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string DurationText => $"{_row.Duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";
    private bool TransportConfirmed => _rawProtocol is "h2" or "h3" &&
        (_httpVersion == "auto" || _httpVersion == _rawProtocol) &&
        (_row.EchRequired ? _row.EchAccepted == true : _row.EchAccepted == false);
    public bool Succeeded => _row.FailurePhase is null && TransportConfirmed && _row.HttpStatus is >= 200 and < 300;
    public bool NeedsApiKey => _row.Source == "tmdb" && _row.HttpStatus == 401 && TransportConfirmed &&
        _row.FailurePhase is null or "http";
    public string OutcomeText => NeedsApiKey ? "需要API密钥" : Succeeded ? "连接正常" : "连接失败";
    public string DetailText
    {
        get
        {
            List<string> details = [];
            if (NeedsApiKey) details.Add("业务认证：需要 TMDB API 密钥（HTTP 401）");
            else if (!string.IsNullOrWhiteSpace(_row.FailurePhase)) details.Add("失败阶段：" + _row.FailurePhase);
            if (_rawProtocol is not ("h2" or "h3")) details.Add("实际 HTTP/2 或 HTTP/3 协议未确认，不能报告成功。");
            if (_httpVersion is "h2" or "h3" && _rawProtocol is "h2" or "h3" && _rawProtocol != _httpVersion)
                details.Add($"实际协议 {Protocol} 不符合配置 {OutboundDirectViewModel.HttpLabel(_httpVersion)}。");
            if (_row.EchRequired && _row.EchAccepted != true) details.Add("此域名要求 ECH，但未确认接受，连接未通过。");
            if (!_row.EchRequired && _row.EchAccepted == true) details.Add("此域名不使用 ECH，实际结果与策略不符。");
            if (!_row.EchRequired && _row.EchAccepted is null) details.Add("普通 TLS 域名缺少 ECH 策略确认，不能报告成功。");
            if (NeedsApiKey) details.Add("源站连接已建立；不记为业务成功。");
            if (_row.HttpStatus is null or < 100 or > 599) details.Add("缺少有效的源站 HTTP 状态，不能报告成功。");
            details.Add(_row.Diagnostic);
            return OutboundDirectViewModel.SafeText(string.Join(Environment.NewLine, details));
        }
    }
}
