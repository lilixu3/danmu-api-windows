using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DanmuApi.App.Services;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.ViewModels;

public sealed record ConfigurationCategory(string Key, string Title, string Description, string Icon);

public sealed record ConfigurationVariableRow(
    string Key,
    string Category,
    string Type,
    string Description,
    string Value,
    string Source,
    string CurrentValueText,
    bool IsConfigured,
    bool IsSensitive,
    CoreEnvDefinition Definition,
    CoreEnvValueState State,
    ConfigurationEditorTemplate EditorTemplate = ConfigurationEditorTemplate.Text,
    string EditorActionText = "编辑");

public sealed class CoreConfigurationChangedEventArgs(string key, DotEnvMutationKind mutationKind) : EventArgs
{
    public string Key { get; } = key;
    public DotEnvMutationKind MutationKind { get; } = mutationKind;
}

public enum ConfigurationEditorTemplate
{
    Text,
    Toggle,
    Number,
    Select,
    MultiSelect,
    Credential,
    Url,
    OrderedList,
    ServerList,
    Mapping,
    Rules,
    ColorPalette,
    OffsetRules,
}

public sealed partial class ConfigurationPageViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly ISettingsStore _settingsStore;
    private readonly IUiDialogService _dialogService;
    private readonly IAdminWriteGate _writeGate;
    private readonly IAdminSessionService? _adminSession;
    private readonly ICoreEnvClient _envClient;
    private readonly IAppDiagnostics _diagnostics;
    private readonly CoreEnvRepository _repository;
    private ManagedCoreVariant _variant;
    private CoreEnvSnapshot? _snapshot;
    private string? _diagnostic;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ConfigurationCategory? _selectedCategory;

    [ObservableProperty]
    private bool _isBusy;

    public ConfigurationPageViewModel(
        AppPaths paths,
        ISettingsStore settingsStore,
        IUiDialogService dialogService,
        IAdminWriteGate writeGate,
        ICoreEnvClient envClient,
        IAdminSessionService adminSession,
        IAppDiagnostics diagnostics)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _writeGate = writeGate ?? throw new ArgumentNullException(nameof(writeGate));
        _envClient = envClient ?? throw new ArgumentNullException(nameof(envClient));
        _adminSession = adminSession ?? throw new ArgumentNullException(nameof(adminSession));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _repository = new CoreEnvRepository(paths.NodeProjectDirectory);
        _variant = ReadVariant();
        Reload();
    }

    /// <summary>Categories are generated from the current core catalog.</summary>
    public ObservableCollection<ConfigurationCategory> Categories { get; } = [];

    public ObservableCollection<ConfigurationVariableRow> FilteredVariables { get; } = [];

    public event EventHandler<CoreConfigurationChangedEventArgs>? CoreConfigurationChanged;

    public string PageTitle => string.IsNullOrWhiteSpace(SearchText)
        ? SelectedCategory?.Title ?? "配置工作台"
        : "搜索结果";
    public string PageSubtitle => string.IsNullOrWhiteSpace(SearchText)
        ? SelectedCategory?.Description
            ?? "分类和变量均来自当前核心 envs.js；点击变量进入对应的编辑模板。"
        : "正在搜索当前核心 envs.js 中声明的全部变量。";
    public string CoreSummary => _snapshot is null
        ? "当前核心配置目录不可用"
        : $"{_snapshot.Variant.ToStorageKey()} · {_snapshot.Definitions.Count} 个变量 · 已配置 {_snapshot.ConfiguredCount} 个";
    public string DiagnosticText => _diagnostic ?? "保存通过管理员门禁、原子事务和回读校验。";
    public bool HasSnapshot => _snapshot is not null;
    public bool HasDiagnostic => !string.IsNullOrWhiteSpace(_diagnostic);
    public bool HasVariables => FilteredVariables.Count > 0;
    public bool IsBusyState => IsBusy;

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private async Task EditVariableAsync(ConfigurationVariableRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        if (row.Definition.IsAdministratorManaged)
        {
            _diagnostic = "ADMIN_TOKEN 必须通过 设置 > 安全 的专用管理员密码流程修改。";
            NotifyState();
            await _dialogService.ShowMessageAsync("管理员密码", _diagnostic, isError: true).ConfigureAwait(true);
            return;
        }

        if (!await _writeGate.EnsureAsync($"修改 {row.Key}").ConfigureAwait(true))
        {
            return;
        }

        Reload();
        if (_snapshot is null || !_snapshot.Values.TryGetValue(row.Key, out var latestState))
        {
            _diagnostic = $"无法读取 {row.Key} 的最新配置状态，拒绝打开编辑器。";
            NotifyState();
            await _dialogService.ShowMessageAsync("配置编辑失败", _diagnostic, isError: true).ConfigureAwait(true);
            return;
        }

        row = ToRow(latestState);
        var current = row.State.IsConfigured
            ? row.State.ConfiguredValue ?? string.Empty
            : row.State.EffectiveValue ?? string.Empty;
        CoreEnvEditResult edit;
        try
        {
            edit = await _dialogService.PromptCoreEnvEditAsync(
                row.Definition,
                current,
                row.State.IsConfigured,
                BuildEditorDescription(row))
                .ConfigureAwait(true);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
        {
            _diagnostics.Record($"打开核心配置编辑器失败：{error.Message}");
            _diagnostic = error.Message;
            NotifyState();
            await _dialogService.ShowMessageAsync("配置编辑失败", _diagnostic, isError: true).ConfigureAwait(true);
            return;
        }

        if (edit.Action is CoreEnvEditAction.Cancel or CoreEnvEditAction.Keep)
        {
            return;
        }

        if (edit.Action == CoreEnvEditAction.Delete &&
            !await _dialogService.ConfirmAsync(
                "恢复默认配置",
                $"删除 {row.Key} 的显式配置后，核心将恢复默认值。",
                "恢复默认").ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (edit.Action == CoreEnvEditAction.Delete)
            {
                await DeleteThroughCoreAsync(row.Key).ConfigureAwait(true);
                OnCoreConfigurationChanged(row.Key, DotEnvMutationKind.Delete);
            }
            else
            {
                var value = edit.Value ?? string.Empty;
                _repository.Apply(_snapshot!, [DotEnvMutation.Set(row.Key, value)]);
                Reload();
                if (_snapshot is null ||
                    !_snapshot.Values.TryGetValue(row.Key, out var savedState) ||
                    !savedState.IsConfigured ||
                    !string.Equals(savedState.ConfiguredValue, value.TrimEnd('\r', '\n'), StringComparison.Ordinal))
                {
                    throw new IOException($"写入 {row.Key} 后回读校验失败，拒绝报告成功");
                }

                OnCoreConfigurationChanged(row.Key, DotEnvMutationKind.Set);
            }

            _diagnostic = edit.Action == CoreEnvEditAction.Delete
                ? "已通过核心删除接口移除 .env 显式值，核心将恢复默认配置。"
                : "配置已写入 .env，核心会通过文件监听热加载。";
            NotifyState();
            await _dialogService.ShowMessageAsync("配置已保存", _diagnostic).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            _diagnostics.Record($"写入核心配置失败：{error.Message}");
            _diagnostic = error.Message;
            NotifyState();
            await _dialogService.ShowMessageAsync("配置保存失败", _diagnostic, isError: true).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteVariableAsync(ConfigurationVariableRow? row)
    {
        if (row is null || IsBusy || !row.IsConfigured)
        {
            return;
        }

        if (row.Definition.IsAdministratorManaged)
        {
            await _dialogService.ShowMessageAsync("管理员密码", "ADMIN_TOKEN 必须通过 设置 > 安全 的专用管理员密码流程修改。", isError: true).ConfigureAwait(true);
            return;
        }

        if (!await _writeGate.EnsureAsync($"删除 {row.Key} 的显式配置").ConfigureAwait(true))
        {
            return;
        }

        if (!await _dialogService.ConfirmAsync(
                "删除配置",
                $"删除 {row.Key} 的显式配置后，核心将恢复默认值。",
                "删除")
            .ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            await DeleteThroughCoreAsync(row.Key).ConfigureAwait(true);
            OnCoreConfigurationChanged(row.Key, DotEnvMutationKind.Delete);
            _diagnostic = $"已通过核心删除接口移除 {row.Key} 的显式配置，核心将使用默认值。";
            NotifyState();
            await _dialogService.ShowMessageAsync("配置已删除", _diagnostic).ConfigureAwait(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            _diagnostics.Record($"删除核心配置失败：{error.Message}");
            _diagnostic = error.Message;
            NotifyState();
            await _dialogService.ShowMessageAsync("配置删除失败", _diagnostic, isError: true).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        RefreshRows();
    }

    partial void OnSelectedCategoryChanged(ConfigurationCategory? value)
    {
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        RefreshRows();
    }

    private void Reload()
    {
        try
        {
            _variant = ReadVariant();
            _snapshot = _repository.ReadSnapshot(_variant);
            _diagnostic = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or DirectoryNotFoundException or FileNotFoundException)
        {
            _snapshot = null;
            _diagnostic = $"配置目录读取失败：{error.Message}";
            _diagnostics.Record(_diagnostic);
        }

        RebuildCategories();
        OnPropertyChanged(nameof(CoreSummary));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(DiagnosticText));
        OnPropertyChanged(nameof(HasDiagnostic));
        RefreshRows();
    }

    private void RebuildCategories()
    {
        var currentKey = SelectedCategory?.Key;
        Categories.Clear();
        var presentCategories = _snapshot?.Definitions
            .Select(definition => definition.Category)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        foreach (var category in presentCategories)
        {
            Categories.Add(new(category, CategoryLabel(category), "当前核心 envs.js 中声明的变量", "\uE713"));
        }

        SelectedCategory = Categories.FirstOrDefault(category => category.Key == currentKey)
            ?? Categories.FirstOrDefault();
    }

    private void RefreshRows()
    {
        FilteredVariables.Clear();
        if (_snapshot is null)
        {
            return;
        }

        var query = SearchText.Trim();
        var definitions = query.Length > 0 || SelectedCategory is null
            ? _snapshot.Definitions
            : _snapshot.Definitions.Where(definition => definition.Category == SelectedCategory.Key);
        foreach (var state in definitions
                     .Select(definition => _snapshot.Values[definition.Key])
                     .Where(value => query.Length == 0 ||
                                     value.Definition.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                     value.Definition.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(state => state.Definition.Key, StringComparer.Ordinal))
        {
            FilteredVariables.Add(ToRow(state));
        }
    }

    public static ConfigurationEditorTemplate ResolveEditorTemplate(CoreEnvDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Key is "SOURCE_ORDER" or "PLATFORM_ORDER")
        {
            return ConfigurationEditorTemplate.OrderedList;
        }

        if (definition.Key == "VOD_SERVERS")
        {
            return ConfigurationEditorTemplate.ServerList;
        }

        if (definition.Key is "MERGE_SOURCE_PAIRS" or "CUSTOM_MERGE_RULES" or "BLOCKED_WORDS" or "IP_BLACKLIST")
        {
            return ConfigurationEditorTemplate.Rules;
        }

        if (definition.Key is "TITLE_MAPPING_TABLE" or "AUTO_MATCH_MAPPING_TABLE")
        {
            return ConfigurationEditorTemplate.Mapping;
        }

        if (definition.Key is "COLOR_POOL" or "GRADIENT_COLORS")
        {
            return ConfigurationEditorTemplate.ColorPalette;
        }

        if (definition.Key == "DANMU_OFFSET")
        {
            return ConfigurationEditorTemplate.OffsetRules;
        }

        if (definition.IsSensitive)
        {
            return ConfigurationEditorTemplate.Credential;
        }

        if (definition.Key.EndsWith("_URL", StringComparison.Ordinal) ||
            definition.Key is "PROXY_URL" or "OTHER_SERVER")
        {
            return ConfigurationEditorTemplate.Url;
        }

        return definition.Type switch
        {
            CoreEnvType.Boolean => ConfigurationEditorTemplate.Toggle,
            CoreEnvType.Number => ConfigurationEditorTemplate.Number,
            CoreEnvType.Select => ConfigurationEditorTemplate.Select,
            CoreEnvType.MultiSelect => ConfigurationEditorTemplate.MultiSelect,
            CoreEnvType.Map => ConfigurationEditorTemplate.Mapping,
            _ => ConfigurationEditorTemplate.Text,
        };
    }

    internal static string FormatCurrentValue(CoreEnvValueState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var value = state.EffectiveValue;
        if (value is null)
        {
            return "当前值：未配置";
        }

        if (value.Length == 0)
        {
            return "当前值：空字符串";
        }

        return state.Definition.IsSensitive
            ? $"当前值：{MaskSensitiveValue(value)}"
            : $"当前值：{value}";
    }

    internal static string MaskSensitiveValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "••••••••";
    }

    private static ConfigurationVariableRow ToRow(CoreEnvValueState state)
    {
        var value = state.EffectiveValue is null
            ? "未配置"
            : state.EffectiveValue.Length == 0
                ? "空字符串"
                : state.Definition.IsSensitive
                    ? MaskSensitiveValue(state.EffectiveValue)
                    : state.EffectiveValue;
        return new ConfigurationVariableRow(
            state.Definition.Key,
            CategoryLabel(state.Definition.Category),
            TypeLabel(state.Definition.Type),
            state.Definition.Description,
            value,
            state.Source switch
            {
                CoreEnvValueSource.ProcessEnvironment => "进程环境",
                CoreEnvValueSource.DotEnv => ".env",
                CoreEnvValueSource.CoreDefault => "核心默认",
                _ => "未配置",
            },
            FormatCurrentValue(state),
            state.IsConfigured,
            state.Definition.IsSensitive,
            state.Definition,
            state,
            ResolveEditorTemplate(state.Definition));
    }

    private static string CategoryLabel(string category) => category switch
    {
        "api" => "API 配置",
        "source" => "数据源配置",
        "match" => "匹配配置",
        "danmu" => "弹幕配置",
        "cache" => "缓存配置",
        "system" => "系统配置",
        _ => category,
    };

    private static string TypeLabel(CoreEnvType type) => type switch
    {
        CoreEnvType.Text => "文本",
        CoreEnvType.Number => "数字",
        CoreEnvType.Boolean => "开关",
        CoreEnvType.Select => "单选",
        CoreEnvType.MultiSelect => "多选",
        CoreEnvType.Map => "规则",
        _ => type.ToString(),
    };

    private static string BuildEditorDescription(ConfigurationVariableRow row)
    {
        var constraints = row.Definition.Minimum is not null || row.Definition.Maximum is not null
            ? $"范围：{row.Definition.Minimum?.ToString() ?? "不限"} - {row.Definition.Maximum?.ToString() ?? "不限"}。"
            : string.Empty;
        var options = row.Definition.Options.Count > 0
            ? $"可选值：{string.Join(", ", row.Definition.Options)}。"
            : string.Empty;
        var warning = row.State.HasProcessOverride ? "当前有效值被进程环境覆盖，保存的 .env 值将在覆盖解除后生效。" : string.Empty;
        return $"{row.Description}\n{constraints}{options}{warning}";
    }

    private async Task DeleteThroughCoreAsync(string key)
    {
        if (_snapshot is null)
        {
            throw new InvalidOperationException("当前没有可用的核心配置快照");
        }

        if (_adminSession is null)
        {
            throw new InvalidOperationException("管理员会话服务不可用，拒绝调用核心删除接口");
        }

        _adminSession.Refresh();
        var adminToken = _adminSession.CurrentAdminTokenOrNull();
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            throw new InvalidOperationException("管理员模式会话已失效，请重新进入管理员模式");
        }

        var config = DesktopConfigReader.Read(_settingsStore, _paths.NodeProjectDirectory);
        var host = config.ListenHost is "0.0.0.0" or "::"
            ? "127.0.0.1"
            : config.ListenHost;
        var token = RuntimeTokenResolver.Resolve(_snapshot.EnvPath);
        var result = await _envClient.DeleteAsync(
            host,
            config.Port,
            token,
            adminToken,
            key).ConfigureAwait(true);
        if (!result.Succeeded)
        {
            throw new IOException(result.Diagnostic);
        }

        Reload();
        if (_snapshot is null || _snapshot.Values[key].IsConfigured)
        {
            throw new IOException($"核心删除接口返回成功，但 {key} 仍存在于 .env，拒绝报告成功");
        }
    }

    private ManagedCoreVariant ReadVariant()
    {
        var values = _settingsStore.Read();
        var variant = values.TryGetValue("variant_override", out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : DotEnvFile.ReadValue(Path.Combine(_paths.NodeProjectDirectory, "config", ".env"), "DANMU_API_VARIANT") ?? "stable";
        return ManagedCoreVariantExtensions.ParseManagedVariant(variant.ToLowerInvariant());
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(DiagnosticText));
        OnPropertyChanged(nameof(HasDiagnostic));
        OnPropertyChanged(nameof(CoreSummary));
        OnPropertyChanged(nameof(HasVariables));
    }

    private void OnCoreConfigurationChanged(string key, DotEnvMutationKind mutationKind) =>
        CoreConfigurationChanged?.Invoke(
            this,
            new CoreConfigurationChangedEventArgs(key, mutationKind));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
