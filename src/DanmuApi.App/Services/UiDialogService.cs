using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DanmuApi.App.Controls;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.App.Services;

public enum ProgressOperationOutcome
{
    Completed,
    Canceled,
    Failed,
}

public sealed record ProgressOperationResult(ProgressOperationOutcome Outcome, string? Diagnostic);

public sealed record GithubTokenDialogResult(bool Cancelled, bool Clear, string? Token)
{
    public static GithubTokenDialogResult Cancel() => new(true, false, null);
    public static GithubTokenDialogResult ClearToken() => new(false, true, null);
    public static GithubTokenDialogResult Submit(string token) => new(false, false, token);
}

public interface IUiDialogService
{
    Task EditPortAsync(MainWindowViewModel viewModel);
    Task EditTokenAsync(MainWindowViewModel viewModel);
    Task ShowCacheAsync(MainWindowViewModel viewModel);
    Task<CloseActionDecision?> AskCloseActionAsync();
    Task<string?> ChooseGithubRouteAsync(
        string reason,
        string selectedProxyId,
        IGithubProxySpeedTester speedTester,
        CancellationToken cancellationToken = default);
    Task<string?> ChooseRuntimeRootAsync(CancellationToken cancellationToken = default);
    Task OpenDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(false);
    Task<bool> ConfirmCoreSetupRequiredAsync(string diagnostic) => Task.FromResult(false);
    Task<bool> ConfirmAdminModeRequiredAsync(string message) => Task.FromResult(false);
    Task<string?> SaveTextFileAsync(string suggestedFileName, string content) => Task.FromResult<string?>(null);
    Task<string?> SaveBytesFileAsync(string suggestedFileName, byte[] content, string contentType) => Task.FromResult<string?>(null);
    Task<string?> PickFolderAsync(string? initialDirectory) => Task.FromResult<string?>(null);
    Task CopyTextAsync(string text);
    Task ShowMessageAsync(string title, string message, bool isError = false) => Task.CompletedTask;
    Task<string?> PromptTextAsync(string title, string description, string initial, string confirmLabel) => Task.FromResult<string?>(null);
    Task<DanmuFavoriteSchedule?> PromptFavoriteScheduleAsync(DanmuFavoriteSchedule? current) => Task.FromResult<DanmuFavoriteSchedule?>(null);
    Task OpenExternalUrlAsync(string url) => Task.CompletedTask;
    Task<ProgressOperationResult> RunWithProgressDialogAsync(
        string title,
        Func<IProgress<CoreInstallProgress>, CancellationToken, Task> operation) =>
        Task.FromResult(new ProgressOperationResult(ProgressOperationOutcome.Failed, "当前环境没有可用的进度弹窗"));
    Task<bool> ShowCommitDetailsAsync(GithubCommitDetails details) => Task.FromResult(false);
    Task<bool> ShowPullRequestDetailsAsync(GithubPullRequest pullRequest, IReadOnlyList<GithubFileChange> files) => Task.FromResult(false);
    Task<bool> ShowUpdateDetailsAsync(GithubCompareResult comparison, string localDisplay, string remoteDisplay) => Task.FromResult(false);
    Task<GithubTokenDialogResult> PromptGithubTokenAsync(bool configured, string hint) => Task.FromResult(GithubTokenDialogResult.Cancel());
    Task<string?> PromptCoreEnvValueAsync(CoreEnvDefinition definition, string initial, string description) =>
        PromptTextAsync($"编辑 {definition.Key}", description, initial, "写入 .env");
    Task<CoreEnvEditResult> PromptCoreEnvEditAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description) =>
        PromptCoreEnvValueAsync(definition, initial, description)
            .ContinueWith(task => task.Result is null
                ? CoreEnvEditResult.Cancel()
                : CoreEnvEditResult.Set(task.Result),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
}

public sealed partial class UiDialogService : IUiDialogService
{
    private readonly Func<Window?> _ownerProvider;
    private readonly AppPaths? _paths;
    private readonly ISettingsStore? _settingsStore;
    private readonly IAdminSessionService? _adminSession;
    private readonly IRuntimeController? _runtimeController;
    private readonly ICoreCredentialClient? _credentialClient;

    public UiDialogService(Func<Window?> ownerProvider)
        : this(ownerProvider, null, null, null, null, null)
    {
    }

    public UiDialogService(
        Func<Window?> ownerProvider,
        AppPaths? paths,
        ISettingsStore? settingsStore,
        IAdminSessionService? adminSession,
        IRuntimeController? runtimeController,
        ICoreCredentialClient? credentialClient)
    {
        _ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
        _paths = paths;
        _settingsStore = settingsStore;
        _adminSession = adminSession;
        _runtimeController = runtimeController;
        _credentialClient = credentialClient;
    }

    public async Task EditPortAsync(MainWindowViewModel viewModel)
    {
        var owner = GetOwner();
        var input = new TextBox
        {
            Text = viewModel.PortText,
            Watermark = "1 - 65535",
            Width = 260,
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.Classes.Add("danger-text");
        var dialog = CreateDialog("修改服务端口", "端口修改后，运行中的服务会重启。", input, error, out var result);
        result.Click += (_, _) =>
        {
            if (!int.TryParse(input.Text, out var port) || port is < 1 or > 65_535)
            {
                error.Text = "端口必须是 1 到 65535 之间的整数。";
                return;
            }

            dialog.Tag = port;
            dialog.Close();
        };
        await dialog.ShowDialog(owner);
        if (dialog.Tag is int portValue)
        {
            await viewModel.ApplyPortAsync(portValue);
        }
    }

    public async Task EditTokenAsync(MainWindowViewModel viewModel)
    {
        var owner = GetOwner();
        var input = new TextBox
        {
            Text = viewModel.IsTokenVisible ? viewModel.TokenDisplay : string.Empty,
            PasswordChar = '•',
            Watermark = "留空保持当前值",
            Width = 320,
        };
        var dialog = CreateDialog("修改访问 Token", "输入新的 Token；留空表示保持当前值不变。", input, null, out var result);
        result.Click += (_, _) =>
        {
            dialog.Tag = input.Text ?? string.Empty;
            dialog.Close();
        };
        await dialog.ShowDialog(owner);
        if (dialog.Tag is string token)
        {
            await viewModel.ApplyTokenAsync(token);
        }
    }

    public async Task ShowCacheAsync(MainWindowViewModel viewModel)
    {
        var owner = GetOwner();
        var selection = new CacheCleanupWindow(viewModel);
        await selection.ShowDialog(owner);
    }

    public async Task<CloseActionDecision?> AskCloseActionAsync()
    {
        var owner = GetOwner();
        var dialog = new CloseActionWindow();
        return await dialog.ShowDialog<CloseActionDecision?>(owner);
    }

    public async Task<string?> ChooseGithubRouteAsync(
        string reason,
        string selectedProxyId,
        IGithubProxySpeedTester speedTester,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new GithubRoutePickerWindow(reason, selectedProxyId, speedTester);
        using var registration = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => dialog.Close()));
        return await dialog.ShowDialog<string?>(GetOwner());
    }

    public async Task<string?> ChooseRuntimeRootAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = GetOwner();
        var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider
            ?? throw new InvalidOperationException("当前窗口没有可用的目录选择器");
        var selected = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择新的运行数据位置",
            AllowMultiple = false,
        });
        cancellationToken.ThrowIfCancellationRequested();
        return selected.Count == 0 ? null : selected[0].TryGetLocalPath();
    }

    public Task OpenDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(fullPath);
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false,
            };
            start.ArgumentList.Add(fullPath);
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new IOException($"无法启动资源管理器：{fullPath}");
        }, cancellationToken);
    }

    public async Task<DanmuFavoriteSchedule?> PromptFavoriteScheduleAsync(DanmuFavoriteSchedule? current)
    {
        var owner = GetOwner();
        var frequency = new ComboBox { ItemsSource = new[] { "daily", "weekly" }, SelectedIndex = current?.Frequency == "weekly" ? 1 : 0, Width = 180 };
        var time = new TextBox { Text = current?.Time ?? "09:00", Watermark = "HH:mm", Width = 180 };
        var weekday = new ComboBox { ItemsSource = new[] { "1 周一", "2 周二", "3 周三", "4 周四", "5 周五", "6 周六", "7 周日" }, SelectedIndex = Math.Clamp((current?.Weekday ?? 1) - 1, 0, 6), Width = 180 };
        var weekdayPanel = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "每周日期" }, weekday } };
        var error = CreateErrorText();
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        cancel.Classes.Add("secondary-action");
        var save = new Button { Content = "保存", IsDefault = true, MinWidth = 88 };
        save.Classes.Add("primary-action");
        var dialog = new Window
        {
            Title = "设置定时刷新",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 12,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = "设置定时刷新", FontSize = 20, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "核心使用 Asia/Shanghai；时间必须是 HH:mm。", TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "频率" }, frequency } },
                    new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "时间" }, time } },
                    weekdayPanel,
                    error,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } },
                },
            },
        };
        frequency.SelectionChanged += (_, _) => weekdayPanel.IsVisible = frequency.SelectedIndex == 1;
        weekdayPanel.IsVisible = frequency.SelectedIndex == 1;
        DanmuFavoriteSchedule? result = null;
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            var normalizedTime = time.Text?.Trim() ?? string.Empty;
            if (!System.Text.RegularExpressions.Regex.IsMatch(normalizedTime, "^(?:[01]\\d|2[0-3]):[0-5]\\d$"))
            {
                error.Text = "时间必须是 HH:mm 格式。";
                return;
            }

            result = new DanmuFavoriteSchedule(
                frequency.SelectedIndex == 1 ? "weekly" : "daily",
                normalizedTime,
                frequency.SelectedIndex == 1 ? weekday.SelectedIndex + 1 : null,
                "Asia/Shanghai",
                null,
                null,
                null,
                null,
                null);
            dialog.Close();
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    public Task OpenExternalUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("来源地址必须是 http 或 https URL", nameof(url));
        }

        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        };
        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new IOException("无法启动系统浏览器");
        return Task.CompletedTask;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmLabel);
        var owner = GetOwner();
        var confirm = new Button { Content = confirmLabel, IsDefault = true, MinWidth = 88 };
        confirm.Classes.Add("primary-action");
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        cancel.Classes.Add("secondary-action");
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                    CreateMutedText(message),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm },
                    },
                },
            },
        };
        var result = false;
        confirm.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return result;
    }

    public async Task<bool> ConfirmCoreSetupRequiredAsync(string diagnostic)
    {        var owner = GetOwner();
        var goInstall = new Button { Content = "前往安装", IsDefault = true, MinWidth = 100 };
        goInstall.Classes.Add("primary-action");
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        cancel.Classes.Add("secondary-action");
        var dialog = new Window
        {
            Title = "核心尚未安装",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = "核心尚未安装", FontSize = 20, FontWeight = FontWeight.SemiBold },
                    CreateMutedText("服务需要 Node 核心才能启动。Windows 端不会自动下载核心，请在核心页选择官方上游或自定义仓库安装。"),
                    CreateMutedText(diagnostic),
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, goInstall },
                    },
                },
            },
        };
        var result = false;
        goInstall.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return result;
    }

    public Task<bool> ConfirmAdminModeRequiredAsync(string message) =>
        ConfirmAsync("需要管理员模式", message, "前往设置");

    public async Task<string?> SaveTextFileAsync(string suggestedFileName, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentNullException.ThrowIfNull(content);
        var owner = GetOwner();
        var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider
            ?? throw new InvalidOperationException("当前窗口没有可用的文件保存选择器");
        var result = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出日志",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "log",
            FileTypeChoices =
            [
                new FilePickerFileType("日志文件") { Patterns = ["*.log"] },
                new FilePickerFileType("文本文件") { Patterns = ["*.txt"] },
                new FilePickerFileType("所有文件") { Patterns = ["*"] },
            ],
        }).ConfigureAwait(true);
        if (result is null)
        {
            return null;
        }

        var target = result.TryGetLocalPath()
            ?? throw new IOException("所选保存位置不是本地文件路径，无法导出日志");
        await File.WriteAllTextAsync(target, content, new UTF8Encoding(false)).ConfigureAwait(true);
        return target;
    }

    public async Task<string?> SaveBytesFileAsync(string suggestedFileName, byte[] content, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var owner = GetOwner();
        var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider
            ?? throw new InvalidOperationException("当前窗口没有可用的文件保存选择器");
        var extension = SuggestedCompoundExtension(suggestedFileName);
        var result = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出弹幕",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension.Length == 0 ? null : extension,
            FileTypeChoices =
            [
                new FilePickerFileType("弹幕文件") { Patterns = [extension.Length == 0 ? "*" : "*." + extension] },
                new FilePickerFileType("所有文件") { Patterns = ["*"] },
            ],
        }).ConfigureAwait(true);
        if (result is null)
        {
            return null;
        }

        var target = result.TryGetLocalPath()
            ?? throw new IOException("所选保存位置不是本地文件路径，无法导出弹幕");
        await File.WriteAllBytesAsync(target, content).ConfigureAwait(true);
        return target;
    }

    private static string SuggestedCompoundExtension(string suggestedFileName)
    {
        var known = DanmuDownloadFormatExtensions.FromFileName(suggestedFileName);
        return known?.Extension() ?? Path.GetExtension(suggestedFileName).TrimStart('.');
    }

    public async Task<string?> PickFolderAsync(string? initialDirectory)
    {
        var owner = GetOwner();
        var storageProvider = TopLevel.GetTopLevel(owner)?.StorageProvider
            ?? throw new InvalidOperationException("当前窗口没有可用的文件夹选择器");
        var options = new FolderPickerOpenOptions
        {
            Title = "选择弹幕保存目录",
            AllowMultiple = false,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(initialDirectory).ConfigureAwait(true);
        }

        var folders = await storageProvider.OpenFolderPickerAsync(options).ConfigureAwait(true);
        if (folders is null || folders.Count == 0)
        {
            return null;
        }

        return folders[0].TryGetLocalPath()
            ?? throw new IOException("所选位置不是本地文件夹路径，无法用作保存目录");
    }

    public async Task CopyTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var owner = GetOwner();
        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
        if (clipboard is null)
        {
            throw new InvalidOperationException("当前窗口没有可用的系统剪贴板");
        }

        await clipboard.SetTextAsync(text);
    }

    public async Task ShowMessageAsync(string title, string message, bool isError = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var owner = GetOwner();
        var ok = new Button { Content = "确定", MinWidth = 88, IsDefault = true, IsCancel = true };
        ok.Classes.Add(isError ? "secondary-action" : "primary-action");
        var messageBlock = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        if (isError)
        {
            messageBlock.Classes.Add("danger-text");
        }

        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                    messageBlock,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { ok },
                    },
                },
            },
        };
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    public async Task<CoreEnvEditResult> PromptCoreEnvEditAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Key is "SOURCE_ORDER" or "PLATFORM_ORDER")
        {
            return await PromptOrderedListAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "VOD_SERVERS")
        {
            return await PromptVodServersAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "TITLE_MAPPING_TABLE")
        {
            return await PromptMappingTableAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "AUTO_MATCH_MAPPING_TABLE")
        {
            return await PromptAutoMatchMappingsAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "MERGE_SOURCE_PAIRS")
        {
            return await PromptMergeSourcePairsAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "CUSTOM_MERGE_RULES")
        {
            return await PromptCustomMergeRulesAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "DANMU_OFFSET")
        {
            return await PromptDanmuOffsetsAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "IP_BLACKLIST")
        {
            return await PromptIpBlacklistAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key is "COLOR_POOL" or "GRADIENT_COLORS")
        {
            return await PromptColorPaletteAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "BLOCKED_WORDS")
        {
            return await PromptLineListAsync(definition, initial, configured, description, ",").ConfigureAwait(true);
        }

        if (definition.Key == "BILIBILI_COOKIE")
        {
            return await PromptBilibiliCookieAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (definition.Key == "AI_API_KEY")
        {
            return await PromptAiApiKeyAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        if (!definition.IsSensitive)
        {
            return await PromptBasicCoreEnvEditAsync(definition, initial, configured, description).ConfigureAwait(true);
        }

        var owner = GetOwner();
        var input = new TextBox
        {
            Text = initial,
            PasswordChar = '•',
            Watermark = "输入配置值",
            Width = 420,
        };
        var visibility = new Button { Content = "显示敏感值", MinWidth = 105 };
        visibility.Classes.Add("secondary-action");
        visibility.Click += (_, _) =>
        {
            var reveal = input.PasswordChar != '\0';
            input.PasswordChar = reveal ? '\0' : '•';
            visibility.Content = reveal ? "隐藏敏感值" : "显示敏感值";
        };
        var status = CreateMutedText("已载入当前真实值；输入框默认遮罩，显示操作仅影响本次编辑窗口。");
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80 };
        cancel.Classes.Add("secondary-action");
        var clear = new Button { Content = "设为空值", MinWidth = 96 };
        clear.Classes.Add("secondary-action");
        var delete = new Button { Content = "恢复默认", MinWidth = 96, IsVisible = configured };
        delete.Classes.Add("danger-action");
        var save = new Button { Content = "替换并保存", IsDefault = true, MinWidth = 110 };
        save.Classes.Add("primary-action");
        var result = CoreEnvEditResult.Cancel();
        var dialog = new Window
        {
            Title = $"编辑 {definition.Key}",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = $"编辑 {definition.Key}", FontSize = 20, FontWeight = FontWeight.SemiBold },
                    CreateMutedText(description),
                    status,
                    input,
                    visibility,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { delete, clear, cancel, save },
                    },
                },
            },
        };
        cancel.Click += (_, _) => dialog.Close();
        clear.Click += (_, _) => { result = CoreEnvEditResult.Set(string.Empty); dialog.Close(); };
        delete.Click += (_, _) => { result = CoreEnvEditResult.Delete(); dialog.Close(); };
        save.Click += (_, _) =>
        {
            result = string.IsNullOrEmpty(input.Text)
                ? CoreEnvEditResult.Keep()
                : CoreEnvEditResult.Set(input.Text);
            dialog.Close();
        };
        await dialog.ShowDialog(owner);
        return result;
    }

    private async Task<CoreEnvEditResult> PromptOrderedListAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        CoreEnvStructuredValidation.Validate(definition, initial);
        var editor = new OrderedTagsEditor(
            definition.Options,
            initial.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            allowComposites: definition.Key == "PLATFORM_ORDER");
        var error = CreateErrorText();
        var dialog = CreateConfigurationEditorDialog($"编辑 {definition.Key}", description, editor, error, out var save);
        dialog.CanReset = configured;
        save.Click += (_, _) =>
        {
            try
            {
                var value = string.Join(',', editor.Values);
                CoreEnvStructuredValidation.Validate(definition, value);
                dialog.Result = CoreEnvEditResult.Set(value);
                dialog.Close();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                error.Text = exception.Message;
            }
        };
        await dialog.ShowDialog(GetOwner());
        return dialog.Result;
    }

    private async Task<CoreEnvEditResult> PromptVodServersAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        CoreEnvStructuredValidation.Validate(definition, initial);
        var editor = new VodServersEditor(initial);
        var error = CreateErrorText();
        var dialog = CreateConfigurationEditorDialog($"编辑 {definition.Key}", description, editor, error, out var save);
        dialog.CanReset = configured;
        save.Click += (_, _) =>
        {
            try
            {
                CoreEnvStructuredValidation.Validate(definition, editor.Value);
                dialog.Result = CoreEnvEditResult.Set(editor.Value);
                dialog.Close();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                error.Text = exception.Message;
            }
        };
        await dialog.ShowDialog(GetOwner());
        return dialog.Result;
    }

    private async Task<CoreEnvEditResult> PromptMappingTableAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        CoreEnvStructuredValidation.Validate(definition, initial);
        var editor = new MappingTableEditor(initial);
        var error = CreateErrorText();
        var dialog = CreateConfigurationEditorDialog($"编辑 {definition.Key}", description, editor, error, out var save);
        dialog.CanReset = configured;
        save.Click += (_, _) =>
        {
            try
            {
                CoreEnvStructuredValidation.Validate(definition, editor.Value);
                dialog.Result = CoreEnvEditResult.Set(editor.Value);
                dialog.Close();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                error.Text = exception.Message;
            }
        };
        await dialog.ShowDialog(GetOwner());
        return dialog.Result;
    }

    private async Task<CoreEnvEditResult> PromptLineListAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description,
        string separator)
    {
        var editor = new LineListEditor(initial, separator);
        var error = CreateErrorText();
        var dialog = CreateConfigurationEditorDialog($"编辑 {definition.Key}", description, editor, error, out var save);
        dialog.CanReset = configured;
        save.Click += (_, _) =>
        {
            try
            {
                CoreEnvStructuredValidation.Validate(definition, editor.Value);
                dialog.Result = CoreEnvEditResult.Set(editor.Value);
                dialog.Close();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                error.Text = exception.Message;
            }
        };
        await dialog.ShowDialog(GetOwner());
        return dialog.Result;
    }

    public async Task<string?> PromptCoreEnvValueAsync(CoreEnvDefinition definition, string initial, string description)
    {
        var result = await PromptBasicCoreEnvEditAsync(
            definition,
            initial,
            configured: false,
            description).ConfigureAwait(true);
        return result.Action == CoreEnvEditAction.Set ? result.Value : null;
    }

    private async Task<CoreEnvEditResult> PromptBasicCoreEnvEditAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (initial.Length > 0)
        {
            CoreEnvRepository.ValidateValue(definition, initial);
        }

        Control input;
        if (definition.Type == CoreEnvType.Boolean)
        {
            input = new ComboBox
            {
                ItemsSource = new[] { "true", "false" },
                SelectedIndex = string.Equals(initial, "false", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                Width = 280,
            };
        }
        else if (definition.Type == CoreEnvType.Select)
        {
            var selected = definition.Options
                .Select((option, index) => (option, index))
                .FirstOrDefault(pair => string.Equals(pair.option, initial, StringComparison.Ordinal)).index;
            input = new ComboBox
            {
                ItemsSource = definition.Options,
                SelectedIndex = selected >= 0 ? selected : 0,
                Width = 360,
            };
        }
        else if (definition.Type == CoreEnvType.MultiSelect)
        {
            input = new TagPicker(
                definition.Options,
                initial.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                compareTokens: true);
        }
        else
        {
            input = new TextBox
            {
                Text = initial,
                Width = 420,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = definition.Type == CoreEnvType.Text || definition.Type == CoreEnvType.Map ? 90 : 32,
            };
        }

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        error.Classes.Add("danger-text");
        var dialog = CreateConfigurationEditorDialog($"编辑 {definition.Key}", description, input, error, out var result);
        dialog.CanReset = configured;
        result.Click += (_, _) =>
        {
            try
            {
                var value = input switch
                {
                    ComboBox combo => combo.SelectedItem?.ToString(),
                    TagPicker picker => string.Join(',', picker.Values),
                    TextBox text => text.Text ?? string.Empty,
                    _ => null,
                };
                if (value is null)
                {
                    error.Text = "请选择一个有效值。";
                    return;
                }

                CoreEnvRepository.ValidateValue(definition, value);
                dialog.Result = CoreEnvEditResult.Set(value);
                dialog.Close();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                error.Text = exception.Message;
            }
        };
        await dialog.ShowDialog(GetOwner());
        return dialog.Result;
    }


    public async Task<ProgressOperationResult> RunWithProgressDialogAsync(
        string title,
        Func<IProgress<CoreInstallProgress>, CancellationToken, Task> operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(operation);
        var owner = GetOwner();
        using var cancellation = new CancellationTokenSource();
        var dialog = new ProgressDialogWindow(title, cancellation.Cancel);
        var progress = new Progress<CoreInstallProgress>(value => dialog.Apply(value));
        var shown = dialog.ShowDialog(owner);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var task = GuardProgressAsync(() => operation(progress, cancellation.Token), cancellation);
        _ = task.ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                dialog.AllowClose();
                dialog.Close();
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
        await shown;
        return await task.ConfigureAwait(true);
    }

    private static async Task<ProgressOperationResult> GuardProgressAsync(
        Func<Task> operation,
        CancellationTokenSource cancellation)
    {
        try
        {
            await operation().ConfigureAwait(true);
            return new ProgressOperationResult(ProgressOperationOutcome.Completed, null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new ProgressOperationResult(ProgressOperationOutcome.Canceled, null);
        }
        catch (Exception error)
        {
            return new ProgressOperationResult(
                ProgressOperationOutcome.Failed,
                $"{error.GetType().Name}: {error.Message}");
        }
    }

    public async Task<bool> ShowCommitDetailsAsync(GithubCommitDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var dialog = new CommitDetailsWindow(details);
        return await dialog.ShowDialog<bool>(GetOwner());
    }

    public async Task<bool> ShowPullRequestDetailsAsync(GithubPullRequest pullRequest, IReadOnlyList<GithubFileChange> files)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(files);
        var dialog = new PullRequestDetailsWindow(pullRequest, files);
        return await dialog.ShowDialog<bool>(GetOwner());
    }

    public async Task<bool> ShowUpdateDetailsAsync(GithubCompareResult comparison, string localDisplay, string remoteDisplay)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var dialog = new UpdateDetailsWindow(comparison, localDisplay, remoteDisplay);
        return await dialog.ShowDialog<bool>(GetOwner());
    }

    public async Task<GithubTokenDialogResult> PromptGithubTokenAsync(bool configured, string hint)
    {
        var owner = GetOwner();
        var input = new TextBox
        {
            PasswordChar = '•',
            Watermark = "粘贴 GitHub Token",
            Width = 360,
        };
        var status = CreateMutedText(configured
            ? $"当前 Token 已配置（{hint}）。输入新 Token 会先验证，留空不会删除。"
            : "当前未配置 GitHub Token。输入后会先验证账号，再保存到 Windows 安全存储。");
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        cancel.Classes.Add("secondary-action");
        var clear = new Button { Content = "清除 Token", MinWidth = 110, IsVisible = configured };
        clear.Classes.Add("danger-action");
        var submit = new Button { Content = "验证并保存", IsDefault = true, MinWidth = 120 };
        submit.Classes.Add("primary-action");
        var dialog = new Window
        {
            Title = "GitHub Token",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = "验证并配置 GitHub Token", FontSize = 20, FontWeight = FontWeight.SemiBold },
                    status,
                    input,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { clear, cancel, submit },
                    },
                },
            },
        };
        GithubTokenDialogResult? result = null;
        clear.Click += (_, _) => { result = GithubTokenDialogResult.ClearToken(); dialog.Close(); };
        cancel.Click += (_, _) => { result = GithubTokenDialogResult.Cancel(); dialog.Close(); };
        submit.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                status.Text = "请输入 Token；留空不会删除现有 Token。";
                status.Classes.Add("danger-text");
                return;
            }

            result = GithubTokenDialogResult.Submit(input.Text);
            dialog.Close();
        };
        await dialog.ShowDialog(owner);
        return result ?? GithubTokenDialogResult.Cancel();
    }

    private static TextBlock CreateMutedText(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("body-muted");
        return block;
    }

    private Window GetOwner() =>
        _ownerProvider() ?? throw new InvalidOperationException("主窗口尚未创建，无法打开对话框");

    private static Window CreateDialog(
        string title,
        string description,
        Control input,
        TextBlock? error,
        out Button result)
    {
        result = new Button { Content = "保存", IsDefault = true, MinWidth = 80 };
        result.Classes.Add("primary-action");
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80 };
        cancel.Classes.Add("secondary-action");
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        cancel.Click += (_, _) => dialog.Close();
        var content = new StackPanel
        {
            Spacing = 14,
            Margin = new Avalonia.Thickness(24),
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold },
                CreateMutedText(description),
                input,
            },
        };
        if (error is not null)
        {
            content.Children.Add(error);
        }

        content.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancel, result },
        });

        dialog.Content = content;
        return dialog;
    }
}

public sealed class EndpointItem
{
    private readonly IUiDialogService _dialogService;
    private readonly bool _tokenVisible;

    public EndpointItem(string title, string hint, string address, bool tokenVisible, IUiDialogService dialogService)
    {
        Title = title;
        Hint = hint;
        Address = address;
        _tokenVisible = tokenVisible;
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        CopyCommand = new AsyncRelayCommand(CopyAsync, () => !string.IsNullOrWhiteSpace(Address));
    }

    public string Title { get; }
    public string Hint { get; }
    public string Address { get; }
    public bool HasAddress => !string.IsNullOrWhiteSpace(Address);
    public string DisplayAddress => string.IsNullOrWhiteSpace(Address)
        ? Hint
        : _tokenVisible ? Address : MaskAddress(Address);
    public IAsyncRelayCommand CopyCommand { get; }

    private Task CopyAsync() => _dialogService.CopyTextAsync(Address);

    private static string MaskAddress(string address)
    {
        var separator = address.LastIndexOf('/');
        return separator < 0 || separator == address.Length - 1
            ? address
            : $"{address[..(separator + 1)]}••••••";
    }
}
