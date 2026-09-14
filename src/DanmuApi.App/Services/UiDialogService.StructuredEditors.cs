using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using DanmuApi.App.Controls;
using DanmuApi.App.Views;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DanmuApi.Core;
using DanmuApi.Runtime;
using QRCoder;

namespace DanmuApi.App.Services;

public sealed partial class UiDialogService
{
    /// <summary>
    /// 给编辑器的"查看最近数据"挂载点装上面板。核心前端在五个位置渲染同一个面板
    /// （多选 / 映射 / 标题过滤 / 偏移 / 合并规则），这里统一走一个入口，保证语义与文案一致。
    /// </summary>
    private void AttachRecentData(ContentControl host, string key, IRecentDataFillTarget? fillTarget)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!RecentDataKeys.Contains(key))
        {
            // 不在核心支持范围内的变量不该出现挂载点；真的出现就报出来，不要静默留空。
            host.Content = CreateMutedText($"最近数据不适用于 {key}。");
            return;
        }

        var split = host.Parent is null ? null : FindSplitHost(host);
        var panel = new RecentDataPanel(
            key,
            CreateAnimeFetch(),
            fillTarget,
            _posterImages is null ? null : url => _posterImages.LoadAsync(url),
            _diagnostics is null ? null : message => _diagnostics.Record(message),
            splitToggle: split?.RecentDataButtonHost is not null);

        host.Content = panel;
        if (split?.RecentDataButtonHost is not null)
        {
            // 把面板的按钮交给编辑器，放进它自己的动作行里（核心：与「添加规则」同行）。
            split.RecentDataButtonHost.Content = panel.ToggleButton;
        }
    }

    /// <summary>宿主所在的可视树里找 <see cref="IRecentDataSplitHost"/>（编辑器本体）。</summary>
    private static IRecentDataSplitHost? FindSplitHost(ContentControl host)
    {
        Avalonia.Visual? current = host;
        while (current is not null)
        {
            if (current is IRecentDataSplitHost split)
            {
                return split;
            }

            current = current.GetVisualParent();
        }

        return null;
    }

    /// <summary>最近数据不可用的原因；null 表示可以发起请求。与凭据验证的判定保持同一套口径。</summary>
    private string? RecentDataUnavailableReason()
    {
        if (_paths is null || _settingsStore is null || _adminSession is null ||
            _runtimeController is null || _animeClient is null)
        {
            return "当前上下文没有配置核心缓存客户端，无法读取最近数据。";
        }

        return _runtimeController.Snapshot.State == DesktopRuntimeState.Running
            ? null
            : "核心服务未运行；最近数据来自核心内存缓存，请先启动服务。";
    }

    private Func<CancellationToken, Task<CoreCacheAnimeResult>>? CreateAnimeFetch()
    {
        if (_animeClient is null)
        {
            return null;
        }

        return async cancellationToken =>
        {
            var unavailable = RecentDataUnavailableReason();
            if (unavailable is not null)
            {
                return CoreCacheAnimeResult.Failure(unavailable);
            }

            try
            {
                _adminSession!.Refresh();
                var adminToken = _adminSession.CurrentAdminTokenOrNull();
                if (string.IsNullOrWhiteSpace(adminToken))
                {
                    return CoreCacheAnimeResult.Failure("管理员模式会话已失效，请重新进入管理员模式。");
                }

                var config = DesktopConfigReader.Read(_settingsStore!, _paths!.NodeProjectDirectory);
                var host = config.ListenHost is "0.0.0.0" or "::"
                    ? "127.0.0.1"
                    : config.ListenHost;
                var token = RuntimeTokenResolver.Resolve(
                    Path.Combine(_paths!.NodeProjectDirectory, "config", ".env"));
                return await _animeClient
                    .FetchAsync(host, config.Port, token, adminToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or FormatException)
            {
                return CoreCacheAnimeResult.Failure(error.Message);
            }
        };
    }

    private async Task<CoreEnvEditResult> PromptMergeSourcePairsAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var groups = CoreEnvStructuredValues.ParseMergeSourcePairs(definition, initial);
        var editor = new MergeSourcePairsEditor(definition.Options, groups);
        AttachRecentData(editor.RecentDataHost, definition.Key, fillTarget: null);
        var error = CreateErrorText();
        var dialog = CreateStructuredDialog(definition, description, editor, error, out var save);
        save.Click += (_, _) =>
        {
            try
            {
                var value = CoreEnvStructuredValues.FormatMergeSourcePairs(editor.Groups);
                _ = CoreEnvStructuredValues.ParseMergeSourcePairs(definition, value);
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

    private async Task<CoreEnvEditResult> PromptCustomMergeRulesAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var editor = new CustomMergeRulesEditor(definition, initial);
        AttachRecentData(editor.RecentDataHost, definition.Key, editor);
        var error = CreateErrorText();
        var dialog = CreateStructuredDialog(definition, description, editor, error, out var save);
        save.Click += (_, _) =>
        {
            try
            {
                // 与核心一致：写的就是「变量值」框里的文本（两边空白裁掉）；
                // 差别在于我们保存前按核心解析器做严格校验，坏规则不会静默落盘。
                var value = editor.Value.Trim();
                GuardAgainstEmptyOverwrite(definition.Key, value, configured);
                editor.Validate();
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

    private async Task<CoreEnvEditResult> PromptDanmuOffsetsAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var editor = new DanmuOffsetEditor(definition, initial);
        AttachRecentData(editor.RecentDataHost, definition.Key, editor);
        var error = CreateErrorText();
        var dialog = CreateStructuredDialog(definition, description, editor, error, out var save);
        save.Click += (_, _) =>
        {
            try
            {
                var value = editor.Value.Trim();
                GuardAgainstEmptyOverwrite(definition.Key, value, configured);
                editor.Validate();
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

    private async Task<CoreEnvEditResult> PromptBilibiliCookieAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var input = new TextBox
        {
            Text = initial,
            PasswordChar = '•',
            Watermark = "输入 Bilibili Cookie",
            Width = 520,
            AcceptsReturn = true,
            MinHeight = 80,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        var status = CreateMutedText(configured
            ? "已载入当前 Cookie 并默认遮罩；可以直接修改、验证或扫码替换。"
            : "可以手动输入，或在核心服务运行时扫码填入。Cookie 仅提交给本机核心验证。");
        var verify = new Button { Content = "验证 Cookie", MinWidth = 105 };
        verify.Classes.Add("secondary-action");
        var qr = new Button { Content = "扫码登录", MinWidth = 96 };
        qr.Classes.Add("secondary-action");
        var onlineReason = CredentialActionUnavailableReason();
        verify.IsEnabled = onlineReason is null;
        qr.IsEnabled = onlineReason is null;
        if (onlineReason is not null)
        {
            status.Text = onlineReason;
        }

        verify.Click += async (_, _) =>
        {
            verify.IsEnabled = false;
            try
            {
                var context = ReadCredentialContext();
                var cookie = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text;
                var result = await _credentialClient!.VerifyBilibiliCookieAsync(
                    context.Host,
                    context.Port,
                    context.Token,
                    context.AdminToken,
                    cookie).ConfigureAwait(true);
                status.Text = result.Diagnostic;
                SetStatusStyle(status, result.RequestSucceeded && result.IsValid);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or FormatException)
            {
                status.Text = error.Message;
                SetStatusStyle(status, success: false);
            }
            finally
            {
                verify.IsEnabled = CredentialActionUnavailableReason() is null;
            }
        };
        qr.Click += async (_, _) =>
        {
            qr.IsEnabled = false;
            try
            {
                var cookie = await PromptBilibiliQrAsync().ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(cookie))
                {
                    input.Text = cookie;
                    status.Text = "扫码登录成功，Cookie 已填入；点击替换并保存后写入 .env。";
                    SetStatusStyle(status, success: true);
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or FormatException)
            {
                status.Text = error.Message;
                SetStatusStyle(status, success: false);
            }
            finally
            {
                qr.IsEnabled = CredentialActionUnavailableReason() is null;
            }
        };

        // 核心 isBilibiliCookie 分支的字段顺序：状态 → 操作按钮 → 「Cookie 值」+ 输入框 → 说明。
        // 差异点：核心是在打开/输入时自动去核心检测（无按钮），我们保留了显式的「验证 Cookie」，
        // 放在「扫码登录」同一行，避免打开编辑器就自动发请求。
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(status);
        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actionRow.Children.Add(verify);
        actionRow.Children.Add(qr);
        content.Children.Add(actionRow);
        content.Children.Add(ConfigForm.Field("Cookie 值", AttachSecretToggle(input)));
        content.Children.Add(ConfigForm.Help("推荐使用扫码登录自动获取，或手动粘贴包含 SESSDATA 和 bili_jct 的完整 Cookie"));

        return await PromptSensitiveCredentialAsync(
            definition,
            configured,
            description,
            input,
            content).ConfigureAwait(true);
    }

    private async Task<CoreEnvEditResult> PromptAiApiKeyAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var input = new TextBox
        {
            Text = initial,
            PasswordChar = '•',
            Watermark = "输入 AI API Key",
            Width = 520,
        };
        var status = CreateMutedText(configured
            ? "已载入当前 API Key 并默认遮罩；可以修改后测试连通性。"
            : "输入值后可先调用本机核心测试连通性，再决定是否保存。验证不会提前写入 .env。");
        var verify = new Button { Content = "测试连通性", MinWidth = 105 };
        verify.Classes.Add("secondary-action");
        var onlineReason = CredentialActionUnavailableReason();
        verify.IsEnabled = onlineReason is null;
        if (onlineReason is not null)
        {
            status.Text = onlineReason;
        }

        verify.Click += async (_, _) =>
        {
            verify.IsEnabled = false;
            try
            {
                var context = ReadCredentialContext();
                var apiKey = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text;
                if (apiKey is null && !configured)
                {
                    throw new FormatException("请先输入 AI API Key");
                }

                var result = await _credentialClient!.VerifyAiAsync(
                    context.Host,
                    context.Port,
                    context.Token,
                    context.AdminToken,
                    apiKey).ConfigureAwait(true);
                status.Text = result.Diagnostic;
                SetStatusStyle(status, result.Succeeded);
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or FormatException)
            {
                status.Text = error.Message;
                SetStatusStyle(status, success: false);
            }
            finally
            {
                verify.IsEnabled = CredentialActionUnavailableReason() is null;
            }
        };

        // 核心 isAiApiKey 分支的字段顺序：「API Key 值」+ 输入框 → 说明 → 状态 → 测试连通性。
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(ConfigForm.Field("API Key 值", AttachSecretToggle(input)));
        content.Children.Add(ConfigForm.Help("支持 OpenAI 兼容的 API，需配合 AI_BASE_URL 和 AI_MODEL 配置使用"));
        content.Children.Add(status);
        var verifyRow = new StackPanel { Orientation = Orientation.Horizontal };
        verifyRow.Children.Add(verify);
        content.Children.Add(verifyRow);

        return await PromptSensitiveCredentialAsync(
            definition,
            configured,
            description,
            input,
            content).ConfigureAwait(true);
    }

    /// <summary>
    /// 凭据类变量的编辑器（BILIBILI_COOKIE / AI_API_KEY）。字段顺序由调用方给的
    /// <paramref name="content"/> 决定——核心前端这两个变量各自的排布不一样，
    /// 所以这里只负责外壳（与其它变量共用同一套 <see cref="Views.ConfigurationEditorWindow"/>）。
    /// </summary>
    private async Task<CoreEnvEditResult> PromptSensitiveCredentialAsync(
        CoreEnvDefinition definition,
        bool configured,
        string description,
        TextBox input,
        Control content)
    {
        var error = CreateErrorText();
        var dialog = CreateConfigurationEditorDialog(definition, description, content, error, out var save);
        save.Click += (_, _) =>
        {
            try
            {
                // 空输入表示"不改"（保留原值）；真要清空用配置列表行的「清除」。
                var text = input.Text ?? string.Empty;
                GuardAgainstEmptyOverwrite(definition.Key, text, configured);
                dialog.Result = string.IsNullOrEmpty(text)
                    ? CoreEnvEditResult.Keep()
                    : CoreEnvEditResult.Set(text);
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

    private async Task<string?> PromptBilibiliQrAsync()
    {
        var context = ReadCredentialContext();
        var generated = await _credentialClient!.GenerateBilibiliQrAsync(
            context.Host,
            context.Port,
            context.Token,
            context.AdminToken).ConfigureAwait(true);
        if (!generated.Succeeded || string.IsNullOrWhiteSpace(generated.Url) || string.IsNullOrWhiteSpace(generated.QrCodeKey))
        {
            throw new IOException(generated.Diagnostic);
        }

        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(generated.Url, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(qrData);
        var png = qrCode.GetGraphic(8);
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new Bitmap(stream);
        var status = CreateMutedText("请使用 Bilibili 客户端扫描二维码。");
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 88 };
        cancel.Classes.Add("secondary-action");
        var dialog = new Window
        {
            Title = "Bilibili 扫码登录",
            Width = 430,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Avalonia.Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "Bilibili 扫码登录", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new Image { Source = bitmap, Width = 256, Height = 256 },
                    status,
                    cancel,
                },
            },
        };
        using var polling = new CancellationTokenSource();
        polling.CancelAfter(TimeSpan.FromMinutes(3));
        cancel.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => polling.Cancel();
        string? cookie = null;
        async Task PollAsync()
        {
            try
            {
                while (!polling.IsCancellationRequested)
                {
                    var current = ReadCredentialContext();
                    var check = await _credentialClient.CheckBilibiliQrAsync(
                        current.Host,
                        current.Port,
                        current.Token,
                        current.AdminToken,
                        generated.QrCodeKey,
                        polling.Token).ConfigureAwait(true);
                    if (!check.Succeeded)
                    {
                        status.Text = check.Diagnostic;
                        SetStatusStyle(status, success: false);
                        return;
                    }

                    status.Text = check.Diagnostic;
                    if (check.Code == 0)
                    {
                        cookie = check.Cookie;
                        SetStatusStyle(status, success: true);
                        await Task.Delay(500, polling.Token).ConfigureAwait(true);
                        dialog.Close();
                        return;
                    }

                    if (check.Code == 86038)
                    {
                        SetStatusStyle(status, success: false);
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), polling.Token).ConfigureAwait(true);
                }
            }
            catch (OperationCanceledException) when (polling.IsCancellationRequested)
            {
                if (dialog.IsVisible)
                {
                    status.Text = "扫码登录已取消或二维码轮询已超时。";
                    SetStatusStyle(status, success: false);
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or FormatException)
            {
                status.Text = error.Message;
                SetStatusStyle(status, success: false);
            }
        }

        Task pollingTask = Task.CompletedTask;
        dialog.Opened += (_, _) => pollingTask = PollAsync();
        await dialog.ShowDialog(GetOwner());
        polling.Cancel();
        await pollingTask.ConfigureAwait(true);
        bitmap.Dispose();
        return cookie;
    }

    private string? CredentialActionUnavailableReason()
    {
        if (_paths is null || _settingsStore is null || _adminSession is null ||
            _runtimeController is null || _credentialClient is null)
        {
            return "当前上下文没有配置核心凭据验证服务。";
        }

        return _runtimeController.Snapshot.State == DesktopRuntimeState.Running
            ? null
            : "核心服务未运行；可以编辑并保存，验证和扫码需要先启动服务。";
    }

    private CredentialContext ReadCredentialContext()
    {
        var unavailable = CredentialActionUnavailableReason();
        if (unavailable is not null)
        {
            throw new InvalidOperationException(unavailable);
        }

        _adminSession!.Refresh();
        var adminToken = _adminSession.CurrentAdminTokenOrNull();
        if (string.IsNullOrWhiteSpace(adminToken))
        {
            throw new InvalidOperationException("管理员模式会话已失效，请关闭弹窗并重新进入管理员模式");
        }

        var config = DesktopConfigReader.Read(_settingsStore!, _paths!.NodeProjectDirectory);
        var host = config.ListenHost is "0.0.0.0" or "::"
            ? "127.0.0.1"
            : config.ListenHost;
        var token = RuntimeTokenResolver.Resolve(
            Path.Combine(_paths.NodeProjectDirectory, "config", ".env"));
        return new CredentialContext(host, config.Port, token, adminToken);
    }

    private static void SetStatusStyle(TextBlock status, bool success)
    {
        status.Classes.Remove("danger-text");
        status.Classes.Remove("success-text");
        status.Classes.Add(success ? "success-text" : "danger-text");
    }

    private async Task<CoreEnvEditResult> PromptColorPaletteAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var editor = new ColorPaletteEditor(definition, initial);
        var dialog = CreateStructuredDialog(definition, description, editor, CreateErrorText(), out var save);
        save.Click += (_, _) =>
        {
            try
            {
                dialog.Result = CoreEnvEditResult.Set(editor.GetValue());
                dialog.Close();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                editor.ErrorMessage = exception.Message;
            }
        };
        await dialog.ShowDialog(GetOwner());
        return dialog.Result;
    }

    private async Task<CoreEnvEditResult> PromptIpBlacklistAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var editor = new IpBlacklistEditor(CoreEnvStructuredValues.ParseIpBlacklist(initial));
        var error = CreateErrorText();
        var dialog = CreateStructuredDialog(definition, description, editor, error, out var save);
        save.Click += (_, _) =>
        {
            try
            {
                var value = CoreEnvStructuredValues.FormatIpBlacklist(editor.Entries);
                var reparsed = CoreEnvStructuredValues.ParseIpBlacklist(value);
                if (!reparsed.Select(entry => entry.Type).SequenceEqual(editor.Entries.Select(entry => entry.Type)))
                {
                    throw new FormatException("IP_BLACKLIST 规则内容与所选类型不一致");
                }

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

    private ConfigurationEditorWindow CreateStructuredDialog(
        CoreEnvDefinition definition,
        string description,
        Control editor,
        TextBlock error,
        out Button save) =>
        CreateConfigurationEditorDialog(definition, description, editor, error, out save);

    /// <summary>
    /// 弹出编辑器。弹窗顶部三个只读字段（变量类别 / 变量名 / 值类型）与底部描述
    /// 都从核心 envs.js 的元数据来，与核心自带前端的 #env-modal 结构一致。
    /// </summary>
    private ConfigurationEditorWindow CreateConfigurationEditorDialog(
        CoreEnvDefinition definition,
        string description,
        Control editor,
        TextBlock error,
        out Button save)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // 传宿主窗口宽度：弹窗宽度必须在显示前定下来，否则 CenterOwner 会按旧尺寸定位，
        // 显示后再改宽度会让窗口偏到左上（用户实测到的现象）。
        var dialog = new ConfigurationEditorWindow(
            ConfigEditorMetadataFactory.From(definition, description),
            editor,
            GetOwner()?.Width);
        save = dialog.SaveActionButton;
        dialog.ErrorMessage = error.Text;
        error.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBlock.TextProperty)
            {
                dialog.ErrorMessage = error.Text;
            }
        };
        return dialog;
    }

    private static TextBlock CreateErrorText()
    {
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        error.Classes.Add("danger-text");
        return error;
    }

    private sealed record CredentialContext(
        string Host,
        int Port,
        string Token,
        string AdminToken);

}
