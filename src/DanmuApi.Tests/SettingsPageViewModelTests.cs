using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Platform;

namespace DanmuApi.Tests;

public sealed partial class SettingsPageViewModelTests
{
    [Theory]
    [InlineData(DesktopNotificationLevel.Off, "off")]
    [InlineData(DesktopNotificationLevel.Updates, "updates")]
    [InlineData(DesktopNotificationLevel.StartupSuccess, "startup_success")]
    [InlineData(DesktopNotificationLevel.All, "all")]
    public void NotificationSelectionPersistsAndReloads(DesktopNotificationLevel level, string stored)
    {
        var settings = new RecordingSettingsStore();
        settings.Values["notification_level"] = "updates";
        var vm = CreateViewModel(settings);
        vm.SelectedNotificationLevelOption = vm.NotificationLevelOptions.Single(x => x.Value == level);
        Assert.Equal(stored, settings.Values["notification_level"]);
        Assert.Equal(level, CreateViewModel(settings).SelectedNotificationLevelOption.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NotificationSaveFailureDoesNotChangeSelectedPreference(bool throws)
    {
        var settings = new RecordingSettingsStore { FailWrites = throws, IgnoreWrites = !throws };
        var vm = CreateViewModel(settings);
        Assert.Equal(DesktopNotificationLevel.All, vm.SelectedNotificationLevelOption.Value);
        vm.SelectedNotificationLevelOption = vm.NotificationLevelOptions.Single(x => x.Value == DesktopNotificationLevel.Off);
        Assert.Equal(DesktopNotificationLevel.All, vm.SelectedNotificationLevelOption.Value);
        Assert.Contains("保存通知偏好失败", vm.Diagnostic);
        Assert.False(settings.Values.ContainsKey("notification_level"));
    }

    [Fact]
    public async Task ManualNotificationTestUsesBypassWhenPreferenceIsOff()
    {
        var settings = new RecordingSettingsStore();
        settings.Values["notification_level"] = "off";
        var transport = new StubNotificationService();
        var vm = new SettingsPageViewModel(settings, new StubAutostartService(),
            new PreferenceDesktopNotificationService(transport, settings),
            new AppPaths(Path.GetTempPath(), Path.GetTempPath()));
        await vm.TestNotificationCommand.ExecuteAsync(null);
        Assert.Equal(1, transport.Calls);
        Assert.Equal("test", vm.NotificationStatusText);
    }

    [Fact]
    public void SelectingCloseActionPersistsImmediatelyWithoutSuccessPrompt()
    {
        var settings = new RecordingSettingsStore();
        var viewModel = CreateViewModel(settings);

        viewModel.SelectedCloseActionOption = viewModel.CloseActionOptions
            .First(option => option.Value == CloseAction.Tray);

        Assert.Equal("tray", settings.Values["close_action"]);
        Assert.Null(viewModel.Diagnostic);
    }

    [Fact]
    public void ChangingUpdatePolicyFieldsPersistsImmediatelyWithoutSuccessPrompt()
    {
        var policyStore = new RecordingPolicyStore();
        var scheduler = new RecordingUpdateScheduler();
        var viewModel = CreateViewModel(new RecordingSettingsStore(), policyStore, scheduler);

        viewModel.ForegroundCheckIntervalMinutes = 30;
        viewModel.BackgroundCheckEnabled = false;
        viewModel.UpdateAction = CoreUpdateAction.Automatic;

        Assert.Equal(3, policyStore.Writes.Count);
        Assert.Equal(TimeSpan.FromMinutes(30), policyStore.Writes[^1].ForegroundInterval);
        Assert.False(policyStore.Writes[^1].BackgroundEnabled);
        Assert.Equal(CoreUpdateAction.Automatic, policyStore.Writes[^1].UpdateAction);
        Assert.Equal(3, scheduler.PolicyChanges);
        Assert.Null(viewModel.Diagnostic);
    }

    [Fact]
    public void InvalidPolicyValueReportsFailureAndDoesNotPersist()
    {
        var policyStore = new RecordingPolicyStore();
        var viewModel = CreateViewModel(new RecordingSettingsStore(), policyStore, new RecordingUpdateScheduler());

        viewModel.ForegroundCheckIntervalMinutes = 1;

        Assert.Empty(policyStore.Writes);
        Assert.NotNull(viewModel.Diagnostic);
        Assert.Contains("保存核心更新设置失败", viewModel.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminLoginUsesConfigurationBranchAndClearsInputOnSuccess()
    {
        var admin = new StubAdminSessionService(adminMode: false, configured: false)
        {
            SetResult = AdminSessionOperationResult.Success("管理员密码已保存，并开启管理员模式"),
        };
        var viewModel = CreateViewModel(new RecordingSettingsStore(), adminSession: admin);

        Assert.True(viewModel.IsAdminFirstSetup);
        Assert.False(viewModel.HasAdminTokenConfigured);
        Assert.Equal("设置密码并进入管理员模式", viewModel.AdminLoginLabel);
        Assert.Contains("没有 ADMIN_TOKEN", viewModel.AdminTokenStatusText, StringComparison.Ordinal);
        viewModel.AdminTokenInput = "secret-pass";
        viewModel.AdminTokenConfirmation = "secret-pass";
        viewModel.AdminLoginCommand.Execute(null);

        Assert.Equal(["secret-pass"], admin.SetTokenInputs);
        Assert.Empty(admin.LoginInputs);
        Assert.Equal(string.Empty, viewModel.AdminTokenInput);
        Assert.True(viewModel.IsAdminMode);
        Assert.Equal("管理员模式已开启", viewModel.AdminModeStatusText);
        Assert.False(viewModel.AdminLoginVisible);
        Assert.Null(viewModel.Diagnostic);
    }

    [Fact]
    public void AdminLoginUsesVerificationBranchWhenConfiguredAndReportsWrongPassword()
    {
        var admin = new StubAdminSessionService(adminMode: false, configured: true)
        {
            LoginResult = AdminSessionOperationResult.Failure("管理员密码不正确"),
        };
        var viewModel = CreateViewModel(new RecordingSettingsStore(), adminSession: admin);

        Assert.Equal("验证并进入管理员模式", viewModel.AdminLoginLabel);
        viewModel.AdminTokenInput = "nope";
        viewModel.AdminLoginCommand.Execute(null);

        Assert.Equal(["nope"], admin.LoginInputs);
        Assert.Empty(admin.SetTokenInputs);
        Assert.Equal("管理员密码不正确", viewModel.Diagnostic);
    }

    [Fact]
    public void FirstAdminSetupRequiresMatchingConfirmationBeforeWriting()
    {
        var admin = new StubAdminSessionService(configured: false)
        {
            SetResult = AdminSessionOperationResult.Success("saved"),
        };
        var viewModel = CreateViewModel(new RecordingSettingsStore(), adminSession: admin);

        viewModel.AdminTokenInput = "new-password";
        viewModel.AdminTokenConfirmation = "different-password";

        Assert.True(viewModel.IsAdminFirstSetup);
        Assert.False(viewModel.CanSubmitAdmin);
        viewModel.AdminLoginCommand.Execute(null);
        Assert.Empty(admin.SetTokenInputs);

        viewModel.AdminTokenConfirmation = "new-password";
        Assert.True(viewModel.CanSubmitAdmin);
        viewModel.AdminLoginCommand.Execute(null);

        Assert.Equal(["new-password"], admin.SetTokenInputs);
        Assert.True(viewModel.IsAdminMode);
        Assert.Equal(string.Empty, viewModel.AdminTokenInput);
        Assert.Equal(string.Empty, viewModel.AdminTokenConfirmation);
    }

    [Fact]
    public void UnconfiguredAdminStateNeverProvidesBuiltInPassword()
    {
        var admin = new StubAdminSessionService(configured: false);
        var viewModel = CreateViewModel(new RecordingSettingsStore(), adminSession: admin);

        Assert.False(viewModel.HasAdminTokenConfigured);
        Assert.False(viewModel.IsAdminMode);
        Assert.True(viewModel.IsAdminFirstSetup);
        Assert.Equal(string.Empty, viewModel.AdminTokenInput);
        Assert.Equal(string.Empty, viewModel.AdminTokenConfirmation);
        Assert.Contains("不会生成或使用内置管理员密码", viewModel.AdminTokenStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.CanSubmitAdmin);
    }

    [Fact]
    public void Ipv6SettingPersistsImmediatelyWithoutSuccessPrompt()
    {
        var settings = new RecordingSettingsStore();
        var viewModel = CreateViewModel(settings);

        viewModel.Ipv6Enabled = true;

        Assert.Equal("true", settings.Values["ipv6_enabled"]);
        Assert.Null(viewModel.Diagnostic);
        Assert.Contains("同时接受 IPv4 与 IPv6", viewModel.Ipv6StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GithubTokenDialogCancelDoesNotChangeStorage()
    {
        var dialogs = new RecordingDialogService
        {
            GithubTokenResult = GithubTokenDialogResult.Cancel(),
        };
        var tokenConfiguration = new RecordingGithubTokenConfigurationService();
        var viewModel = CreateViewModel(
            new RecordingSettingsStore(),
            dialogs: dialogs,
            tokenConfiguration: tokenConfiguration);

        await viewModel.ConfigureGithubTokenCommand.ExecuteAsync(null);

        Assert.Equal(0, tokenConfiguration.SaveCalls);
        Assert.Equal(0, tokenConfiguration.ClearCalls);
        Assert.Null(viewModel.Diagnostic);
    }

    [Fact]
    public async Task GithubTokenSubmitUsesValidationServiceAndUpdatesMaskedStatus()
    {
        var dialogs = new RecordingDialogService
        {
            GithubTokenResult = GithubTokenDialogResult.Submit("new-secret-token"),
        };
        var tokenConfiguration = new RecordingGithubTokenConfigurationService
        {
            SaveResult = new GithubTokenConfigurationResult(
                true,
                true,
                "saved",
                new GithubTokenConfigurationState(true, "ne••••en", null)),
        };
        var viewModel = CreateViewModel(
            new RecordingSettingsStore(),
            dialogs: dialogs,
            tokenConfiguration: tokenConfiguration);

        await viewModel.ConfigureGithubTokenCommand.ExecuteAsync(null);

        Assert.Equal(["new-secret-token"], tokenConfiguration.SavedTokens);
        Assert.Contains("ne••••en", viewModel.GithubTokenStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("new-secret-token", viewModel.GithubTokenStatusText, StringComparison.Ordinal);
        Assert.Null(viewModel.Diagnostic);
    }

    [Fact]
    public void AdminLogoutFlipsStateText()
    {
        var admin = new StubAdminSessionService(adminMode: true, configured: true, sessionToken: "tok");
        var viewModel = CreateViewModel(new RecordingSettingsStore(), adminSession: admin);

        Assert.True(viewModel.IsAdminMode);
        viewModel.AdminLogoutCommand.Execute(null);

        Assert.Equal(1, admin.LogoutCalls);
        Assert.False(viewModel.IsAdminMode);
        Assert.True(viewModel.IsAdminLoginRequired);
        Assert.Equal("管理员密码已配置，当前处于普通模式", viewModel.AdminModeStatusText);
    }

    private static SettingsPageViewModel CreateViewModel(
        RecordingSettingsStore settings,
        ICoreUpdatePolicyStore? policyStore = null,
        ICoreUpdateScheduler? scheduler = null,
        IAdminSessionService? adminSession = null,
        RecordingDialogService? dialogs = null,
        IGithubTokenConfigurationService? tokenConfiguration = null) =>
        new(
            settings,
            new StubAutostartService(),
            new StubNotificationService(),
            new AppPaths(Path.Combine(Path.GetTempPath(), $"danmu-settings-{Guid.NewGuid():N}"), Path.GetTempPath()),
            dialogs,
            policyStore,
            scheduler,
            adminSession: adminSession,
            githubTokenConfiguration: tokenConfiguration);

    private sealed class RecordingGithubTokenConfigurationService : IGithubTokenConfigurationService
    {
        public GithubTokenConfigurationState State { get; set; } = new(false, "未配置", null);
        public GithubTokenConfigurationResult? SaveResult { get; set; }
        public List<string> SavedTokens { get; } = [];
        public int SaveCalls => SavedTokens.Count;
        public int ClearCalls { get; private set; }

        public GithubTokenConfigurationState GetState() => State;

        public Task<GithubTokenConfigurationResult> ValidateAndSaveAsync(
            string token,
            CancellationToken cancellationToken = default)
        {
            SavedTokens.Add(token);
            var result = SaveResult ?? new GithubTokenConfigurationResult(true, true, "saved", new(true, "ma••••ed", null));
            State = result.State;
            return Task.FromResult(result);
        }

        public Task<GithubTokenConfigurationResult> ClearAsync(CancellationToken cancellationToken = default)
        {
            ClearCalls++;
            State = new GithubTokenConfigurationState(false, "未配置", null);
            return Task.FromResult(new GithubTokenConfigurationResult(true, true, "cleared", State));
        }
    }

    private sealed class RecordingSettingsStore : ISettingsStore
    {
        public bool FailWrites { get; init; }
        public bool IgnoreWrites { get; init; }
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, string> Read() => Values;

        public void Write(IReadOnlyDictionary<string, string?> changes)
        {
            if (FailWrites) throw new IOException("write failure");
            if (IgnoreWrites) return;
            foreach (var (key, value) in changes)
            {
                if (value is null)
                {
                    Values.Remove(key);
                }
                else
                {
                    Values[key] = value;
                }
            }
        }
    }

    private sealed class RecordingPolicyStore : ICoreUpdatePolicyStore
    {
        public List<CoreUpdateScheduleOptions> Writes { get; } = [];
        public CoreUpdateScheduleOptions Read() => CoreUpdateScheduleOptions.Default;

        public void Write(CoreUpdateScheduleOptions options)
        {
            options.Validate();
            Writes.Add(options);
        }
    }

    private sealed class RecordingUpdateScheduler : ICoreUpdateScheduler
    {
        public int PolicyChanges { get; private set; }

        #pragma warning disable CS0067
        public event EventHandler<CoreUpdateCheckResult>? CheckCompleted;
        public event EventHandler<string>? DiagnosticChanged;
        #pragma warning restore CS0067

        public void Start() { }
        public void SetBackgroundActive(bool active) { }
        public void NotifyPolicyChanged() => PolicyChanges++;
        public Task<CoreUpdateCheckResult> CheckForegroundAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<CoreUpdateCheckResult?> CheckBackgroundAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CoreUpdateCheckResult?>(null);
        public Task<CoreUpdateCheckResult> CheckManualAsync(ManagedCoreVariant variant, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubAutostartService : IAutostartService
    {
        public AutostartStatus GetStatus() => new(false, false, "test");
        public Task<AutostartOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.FromResult(AutostartOperationResult.Failure(new AutostartStatus(false, false, "test"), "unused"));
        public AutostartOperationResult RefreshIfEnabled() =>
            AutostartOperationResult.Failure(new AutostartStatus(false, false, "test"), "unused");
    }

    private sealed class StubNotificationService : IDesktopNotificationService
    {
        public int Calls { get; private set; }
        public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DesktopNotificationResult(true, "test"));
        }
    }
}
