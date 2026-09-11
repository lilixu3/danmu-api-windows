using Avalonia.Controls;
using Avalonia.Layout;
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
    private async Task<CoreEnvEditResult> PromptMergeSourcePairsAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var groups = CoreEnvStructuredValues.ParseMergeSourcePairs(definition, initial);
        var editor = new MergeSourcePairsEditor(definition.Options, groups);
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
        var editor = new CustomMergeRulesEditor(
            definition.Sources,
            CoreEnvStructuredValues.ParseCustomMergeRules(definition, initial));
        var error = CreateErrorText();
        var dialog = CreateStructuredDialog(definition, description, editor, error, out var save);
        save.Click += (_, _) =>
        {
            try
            {
                var value = CoreEnvStructuredValues.FormatCustomMergeRules(editor.Rules);
                _ = CoreEnvStructuredValues.ParseCustomMergeRules(definition, value);
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

    private async Task<CoreEnvEditResult> PromptAutoMatchMappingsAsync(
        CoreEnvDefinition definition,
        string initial,
        bool configured,
        string description)
    {
        var editor = new AutoMatchMappingEditor(
            definition.Sources,
            CoreEnvStructuredValues.ParseAutoMatchMappings(definition, initial));
        var error = CreateErrorText();
        var dialog = CreateStructuredDialog(definition, description, editor, error, out var save);
        save.Click += (_, _) =>
        {
            try
            {
                var value = CoreEnvStructuredValues.FormatAutoMatchMappings(definition, editor.Rules);
                _ = CoreEnvStructuredValues.ParseAutoMatchMappings(definition, value);
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
        var editor = new DanmuOffsetEditor(
            definition.Sources,
            CoreEnvStructuredValues.ParseDanmuOffsets(definition, initial));
        var error = CreateErrorText();
        var dialog = CreateStructuredDialog(definition, description, editor, error, out var save);
        save.Click += (_, _) =>
        {
            try
            {
                var value = CoreEnvStructuredValues.FormatDanmuOffsets(editor.Rules);
                _ = CoreEnvStructuredValues.ParseDanmuOffsets(definition, value);
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

        return await PromptSensitiveCredentialAsync(
            definition,
            configured,
            description,
            input,
            status,
            [verify, qr]).ConfigureAwait(true);
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

        return await PromptSensitiveCredentialAsync(
            definition,
            configured,
            description,
            input,
            status,
            [verify]).ConfigureAwait(true);
    }

    private async Task<CoreEnvEditResult> PromptSensitiveCredentialAsync(
        CoreEnvDefinition definition,
        bool configured,
        string description,
        TextBox input,
        TextBlock status,
        IReadOnlyList<Button> auxiliaryButtons)
    {
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 80 };
        cancel.Classes.Add("secondary-action");
        // 只保留取消/保存：「恢复默认」与「设为空值」由配置列表行的「清除」按钮承担。
        var save = new Button { Content = "替换并保存", IsDefault = true, MinWidth = 110 };
        save.Classes.Add("primary-action");
        // 显示/隐藏改为输入框内的眼睛图标，不再单占一个按钮位。
        var secretEditor = AttachSecretToggle(input);
        var auxiliary = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        foreach (var button in auxiliaryButtons)
        {
            auxiliary.Children.Add(button);
        }

        var result = CoreEnvEditResult.Cancel();
        var dialog = new Window
        {
            Title = $"编辑 {definition.Key}",
            Width = 640,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Avalonia.Thickness(24),
                Children =
                {
                    new TextBlock { Text = $"编辑 {definition.Key}", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    CreateMutedText(description),
                    secretEditor,
                    auxiliary,
                    status,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, save },
                    },
                },
            },
        };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            result = string.IsNullOrEmpty(input.Text)
                ? CoreEnvEditResult.Keep()
                : CoreEnvEditResult.Set(input.Text);
            dialog.Close();
        };
        await dialog.ShowDialog(GetOwner());
        return result;
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

    private static ConfigurationEditorWindow CreateStructuredDialog(
        CoreEnvDefinition definition,
        string description,
        Control editor,
        TextBlock error,
        out Button save) =>
        CreateConfigurationEditorDialog($"编辑 {definition.Key}", description, editor, error, out save);

    private static ConfigurationEditorWindow CreateConfigurationEditorDialog(
        string title,
        string description,
        Control editor,
        TextBlock error,
        out Button save)
    {
        var dialog = new ConfigurationEditorWindow(title, description, editor);
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
