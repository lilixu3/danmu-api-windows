using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class ConfigurationPageViewModelTests
{
    [Fact]
    public void LoadsDynamicCatalogAndMasksSensitiveValues()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();


        Assert.True(viewModel.HasSnapshot);
        Assert.Contains("stable", viewModel.CoreSummary, StringComparison.Ordinal);
        var secret = FindVariable(viewModel, "TEST_SECRET_VALUE");
        Assert.True(secret.IsSensitive);
        Assert.Equal("••••••••", secret.Value);
        Assert.Equal("当前值：••••••••", secret.CurrentValueText);
        Assert.Equal(".env", secret.Source);
        var count = FindVariable(viewModel, "TEST_COUNT");
        Assert.Equal("5", count.Value);
        Assert.Equal("当前值：5", count.CurrentValueText);
        Assert.Equal(".env", count.Source);
        var mode = FindVariable(viewModel, "TEST_MODE");
        Assert.Equal("fast", mode.Value);
        Assert.Equal("当前值：fast", mode.CurrentValueText);
        Assert.Equal("核心默认", mode.Source);
        Assert.Equal("数据源配置", FindVariable(viewModel, "VOD_SERVERS").Category);
    }

    [Fact]
    public async Task RefusesWriteWhenAdminGateDenies()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel(allowAdmin: false);
        var row = FindVariable(viewModel, "TEST_COUNT");

        await viewModel.EditVariableCommand.ExecuteAsync(row);

        Assert.Contains("修改 TEST_COUNT", fixture.Gate.Actions);
        Assert.Empty(fixture.Dialogs.CoreEnvEditRequests);
        Assert.Equal("5", DotEnvFile.ReadValue(fixture.EnvPath, "TEST_COUNT"));
    }

    [Fact]
    public async Task EditReadsLatestDotEnvValueInsteadOfStaleRowSnapshot()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "TEST_COUNT");
        File.WriteAllText(fixture.EnvPath, "TEST_SECRET_VALUE=hidden-secret\nTEST_COUNT=8\nVOD_SERVERS=主站@https://example.com\n", new System.Text.UTF8Encoding(false));
        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Keep();

        await viewModel.EditVariableCommand.ExecuteAsync(row);

        var request = Assert.Single(fixture.Dialogs.CoreEnvEditRequests);
        Assert.Equal("8", request.Initial);
    }

    [Fact]
    public async Task WritesValueThroughTransactionAndReloadsSnapshot()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "TEST_COUNT");
        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Set("7");

        await viewModel.EditVariableCommand.ExecuteAsync(row);

        Assert.Equal("7", DotEnvFile.ReadValue(fixture.EnvPath, "TEST_COUNT"));
        Assert.Contains(fixture.Dialogs.Messages, message =>
            message.Title == "配置已保存" && !message.IsError);
        Assert.Equal("7", FindVariable(viewModel, "TEST_COUNT").Value);
    }

    [Fact]
    public async Task SuccessfulSetAndDeletePublishOnlyAfterVerifiedMutation()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var changes = new List<CoreConfigurationChangedEventArgs>();
        viewModel.CoreConfigurationChanged += (_, args) => changes.Add(args);
        var row = FindVariable(viewModel, "TEST_COUNT");

        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Set("7");
        await viewModel.EditVariableCommand.ExecuteAsync(row);

        Assert.Collection(changes, change =>
        {
            Assert.Equal("TEST_COUNT", change.Key);
            Assert.Equal(DotEnvMutationKind.Set, change.MutationKind);
        });

        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Keep();
        await viewModel.EditVariableCommand.ExecuteAsync(FindVariable(viewModel, "TEST_COUNT"));
        Assert.Single(changes);

        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Delete();
        fixture.Dialogs.Confirmation = true;
        fixture.EnvClient.OnDelete = key =>
        {
            var current = DotEnvFile.GetFingerprint(fixture.EnvPath);
            DotEnvFile.ApplyMutations(fixture.EnvPath, current, [DotEnvMutation.Delete(key)]);
        };
        await viewModel.EditVariableCommand.ExecuteAsync(FindVariable(viewModel, "TEST_COUNT"));

        Assert.Collection(changes,
            change => Assert.Equal(DotEnvMutationKind.Set, change.MutationKind),
            change =>
            {
                Assert.Equal("TEST_COUNT", change.Key);
                Assert.Equal(DotEnvMutationKind.Delete, change.MutationKind);
            });
    }

    [Fact]
    public async Task FailedOrCancelledMutationDoesNotPublishChange()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var changes = 0;
        viewModel.CoreConfigurationChanged += (_, _) => changes++;
        var row = FindVariable(viewModel, "TEST_COUNT");

        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Keep();
        await viewModel.EditVariableCommand.ExecuteAsync(row);
        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Set("invalid");
        await viewModel.EditVariableCommand.ExecuteAsync(row);

        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task SensitiveEditorLoadsLatestRealValueAndKeepsItWhenUnchanged()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "TEST_SECRET_VALUE");
        File.WriteAllText(fixture.EnvPath, "TEST_SECRET_VALUE=latest-secret\nTEST_COUNT=5\nVOD_SERVERS=主站@https://example.com\n", new System.Text.UTF8Encoding(false));
        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Keep();

        await viewModel.EditVariableCommand.ExecuteAsync(row);

        var request = Assert.Single(fixture.Dialogs.CoreEnvEditRequests);
        Assert.Equal("TEST_SECRET_VALUE", request.Key);
        Assert.Equal("latest-secret", request.Initial);
        Assert.Equal("latest-secret", DotEnvFile.ReadValue(fixture.EnvPath, "TEST_SECRET_VALUE"));
    }

    [Fact]
    public async Task DeleteRemovesExplicitValueSoCoreDefaultApplies()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "TEST_COUNT");
        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Delete();
        fixture.Dialogs.Confirmation = true;
        fixture.EnvClient.OnDelete = key =>
        {
            var current = DotEnvFile.GetFingerprint(fixture.EnvPath);
            DotEnvFile.ApplyMutations(fixture.EnvPath, current, [DotEnvMutation.Delete(key)]);
        };

        await viewModel.EditVariableCommand.ExecuteAsync(row);

        Assert.Null(DotEnvFile.ReadValue(fixture.EnvPath, "TEST_COUNT"));
        Assert.Contains(fixture.Dialogs.Messages, message =>
            message.Title == "配置已保存" && message.Message.Contains("恢复默认", StringComparison.Ordinal));
        var request = Assert.Single(fixture.EnvClient.Requests);
        Assert.Equal("127.0.0.1", request.Host);
        Assert.Equal(9321, request.Port);
        Assert.Equal("admin-session", request.AdminToken);
        Assert.Equal("TEST_COUNT", request.Key);
    }

    [Fact]
    public async Task BlocksAdminTokenEditingWithoutTouchingDisk()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "ADMIN_TOKEN");

        await viewModel.EditVariableCommand.ExecuteAsync(row);

        Assert.Empty(fixture.Gate.Actions);
        Assert.Empty(fixture.Dialogs.CoreEnvEditRequests);
        Assert.Contains(fixture.Dialogs.Messages, message => message.IsError);
    }

    [Fact]
    public async Task StructuredValidationFailureKeepsFileUnchanged()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "VOD_SERVERS");
        fixture.Dialogs.CoreEnvEdit = CoreEnvEditResult.Set("broken-url");

        await viewModel.EditVariableCommand.ExecuteAsync(row);

        Assert.Contains(fixture.Dialogs.Messages, message =>
            message.Title == "配置保存失败" && message.IsError);
        Assert.Equal("主站@https://example.com", DotEnvFile.ReadValue(fixture.EnvPath, "VOD_SERVERS"));
    }

    [Fact]
    public async Task EditorInitializationFailureIsReportedAndKeepsFileUnchanged()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "VOD_SERVERS");
        fixture.Dialogs.CoreEnvEditException = new FormatException("VOD_SERVERS 包含无效 URL");

        await viewModel.EditVariableCommand.ExecuteAsync(row);

        Assert.Equal("主站@https://example.com", DotEnvFile.ReadValue(fixture.EnvPath, "VOD_SERVERS"));
        Assert.Contains(fixture.Dialogs.Messages, message =>
            message.Title == "配置编辑失败" &&
            message.IsError &&
            message.Message.Contains("无效 URL", StringComparison.Ordinal));
        Assert.Contains("打开核心配置编辑器失败", fixture.Diagnostics.LastDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreCategoryNavigationShowsAllVariablesInThatCategory()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var sourceCategory = viewModel.Categories.Single(category => category.Key == "source");

        viewModel.SelectedCategory = sourceCategory;

        Assert.Equal("数据源配置", viewModel.PageTitle);
        Assert.NotEmpty(viewModel.FilteredVariables);
        Assert.All(viewModel.FilteredVariables, row => Assert.Equal("数据源配置", row.Category));
        Assert.Contains(viewModel.FilteredVariables, row => row.Key == "VOD_SERVERS");
        Assert.DoesNotContain(viewModel.FilteredVariables, row => row.Key == "TEST_COUNT");
        Assert.Equal(4, viewModel.Categories.Count);
    }

    [Fact]
    public void SearchFindsVariablesAcrossCategoriesAndClearingRestoresSelectedCategory()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var sourceCategory = viewModel.Categories.Single(category => category.Key == "source");
        viewModel.SelectedCategory = sourceCategory;

        viewModel.SearchText = "TEST_COUNT";

        var match = Assert.Single(viewModel.FilteredVariables);
        Assert.Equal("TEST_COUNT", match.Key);
        Assert.Equal("缓存配置", match.Category);
        Assert.Equal("搜索结果", viewModel.PageTitle);
        Assert.Contains("全部变量", viewModel.PageSubtitle, StringComparison.Ordinal);

        viewModel.SearchText = string.Empty;

        Assert.Equal("数据源配置", viewModel.PageTitle);
        Assert.All(viewModel.FilteredVariables, row => Assert.Equal("数据源配置", row.Category));
        Assert.Contains(viewModel.FilteredVariables, row => row.Key == "VOD_SERVERS");
        Assert.DoesNotContain(viewModel.FilteredVariables, row => row.Key == "TEST_COUNT");
    }

    [Fact]
    public void EditorTemplatesDoNotCreateNavigationCategories()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var sourceCategory = viewModel.Categories.Single(category => category.Key == "source");
        viewModel.SelectedCategory = sourceCategory;
        var vod = viewModel.FilteredVariables.Single(row => row.Key == "VOD_SERVERS");

        Assert.Equal(ConfigurationEditorTemplate.ServerList, vod.EditorTemplate);
        Assert.Equal("source", viewModel.Categories.Single(category => category.Key == "source").Key);
        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "vod");
        Assert.DoesNotContain(viewModel.Categories, category => category.Key == "quick");
    }

    [Fact]
    public void ResolvesDedicatedAndReusableEditorTemplatesWithoutExtraCategories()
    {
        Assert.Equal(
            ConfigurationEditorTemplate.ColorPalette,
            ConfigurationPageViewModel.ResolveEditorTemplate(Definition("COLOR_POOL", CoreEnvType.Text)));
        Assert.Equal(
            ConfigurationEditorTemplate.ColorPalette,
            ConfigurationPageViewModel.ResolveEditorTemplate(Definition("GRADIENT_COLORS", CoreEnvType.Text)));
        Assert.Equal(
            ConfigurationEditorTemplate.Credential,
            ConfigurationPageViewModel.ResolveEditorTemplate(Definition("BILIBILI_COOKIE", CoreEnvType.Text, sensitive: true)));
        Assert.Equal(
            ConfigurationEditorTemplate.Credential,
            ConfigurationPageViewModel.ResolveEditorTemplate(Definition("AI_API_KEY", CoreEnvType.Text, sensitive: true)));
        Assert.Equal(
            ConfigurationEditorTemplate.Select,
            ConfigurationPageViewModel.ResolveEditorTemplate(Definition("CONVERT_COLOR", CoreEnvType.Select)));
        Assert.Equal(
            ConfigurationEditorTemplate.Toggle,
            ConfigurationPageViewModel.ResolveEditorTemplate(Definition("FUTURE_SWITCH", CoreEnvType.Boolean)));
        Assert.Equal(
            ConfigurationEditorTemplate.Number,
            ConfigurationPageViewModel.ResolveEditorTemplate(Definition("FUTURE_COUNT", CoreEnvType.Number)));
        Assert.Equal(
            ConfigurationEditorTemplate.MultiSelect,
            ConfigurationPageViewModel.ResolveEditorTemplate(Definition("FUTURE_ORDER", CoreEnvType.MultiSelect)));
    }

    [Fact]
    public async Task DeleteButtonCallsCoreDeleteAfterConfirmation()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "TEST_COUNT");
        fixture.Dialogs.Confirmation = true;
        fixture.EnvClient.OnDelete = key =>
        {
            var current = DotEnvFile.GetFingerprint(fixture.EnvPath);
            DotEnvFile.ApplyMutations(fixture.EnvPath, current, [DotEnvMutation.Delete(key)]);
        };

        await viewModel.DeleteVariableCommand.ExecuteAsync(row);

        var request = Assert.Single(fixture.EnvClient.Requests);
        Assert.Equal("TEST_COUNT", request.Key);
        Assert.Equal("admin-session", request.AdminToken);
        Assert.Null(DotEnvFile.ReadValue(fixture.EnvPath, "TEST_COUNT"));
    }

    [Fact]
    public async Task DeleteButtonCancellationDoesNotCallCore()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "TEST_COUNT");
        fixture.Dialogs.Confirmation = false;

        await viewModel.DeleteVariableCommand.ExecuteAsync(row);

        Assert.Empty(fixture.EnvClient.Requests);
        Assert.Equal("5", DotEnvFile.ReadValue(fixture.EnvPath, "TEST_COUNT"));
    }

    [Fact]
    public async Task DeleteCoreFailureKeepsFileAndReportsDiagnostic()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "TEST_COUNT");
        fixture.Dialogs.Confirmation = true;
        fixture.EnvClient.Succeeded = false;
        fixture.EnvClient.Diagnostic = "核心删除接口返回 HTTP 500";

        await viewModel.DeleteVariableCommand.ExecuteAsync(row);

        Assert.Equal("5", DotEnvFile.ReadValue(fixture.EnvPath, "TEST_COUNT"));
        Assert.Contains(fixture.Dialogs.Messages, message =>
            message.Title == "配置删除失败" &&
            message.IsError &&
            message.Message.Contains("HTTP 500", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Dialogs.Messages, message =>
            message.Title == "配置已删除");
        Assert.Contains("HTTP 500", fixture.Diagnostics.LastDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRejectsCoreSuccessWhenFileWasNotChanged()
    {
        using var fixture = new ConfigurationFixture();
        var viewModel = fixture.CreateViewModel();
        var row = FindVariable(viewModel, "TEST_COUNT");
        fixture.Dialogs.Confirmation = true;

        await viewModel.DeleteVariableCommand.ExecuteAsync(row);

        Assert.Equal("5", DotEnvFile.ReadValue(fixture.EnvPath, "TEST_COUNT"));
        Assert.Contains(fixture.Dialogs.Messages, message =>
            message.Title == "配置删除失败" &&
            message.IsError &&
            message.Message.Contains("仍存在于 .env", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Dialogs.Messages, message =>
            message.Title == "配置已删除");
        Assert.Contains("仍存在于 .env", fixture.Diagnostics.LastDiagnostic, StringComparison.Ordinal);
    }

    private static CoreEnvDefinition Definition(
        string key,
        CoreEnvType type,
        bool sensitive = false) =>
        new(
            key,
            "test",
            type,
            key,
            [],
            [],
            null,
            null,
            null,
            sensitive,
            false);

    private static ConfigurationVariableRow FindVariable(ConfigurationPageViewModel viewModel, string key)
    {
        foreach (var category in viewModel.Categories)
        {
            viewModel.SelectedCategory = category;
            var row = viewModel.FilteredVariables.SingleOrDefault(item => item.Key == key);
            if (row is not null)
            {
                return row;
            }
        }

        throw new Xunit.Sdk.XunitException($"未找到配置变量：{key}");
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();

        public ConfigurationFixture()
        {
            Root = Path.Combine(_directory.Path, "runtime-root");
            var project = Path.Combine(Root, "runtime", "nodejs-project");
            var catalogDirectory = Path.Combine(project, "danmu_api_stable", "configs");
            Directory.CreateDirectory(catalogDirectory);
            File.WriteAllText(Path.Combine(catalogDirectory, "envs.js"), """
                const envVarConfig = {
                  'TEST_SECRET_VALUE': { category: 'api', type: 'text', description: '敏感测试值' },
                  'TEST_COUNT': { category: 'cache', type: 'number', description: '数量', min: 0, max: 10 },
                  'TEST_MODE': { category: 'system', type: 'select', options: ['fast', 'safe'], description: '模式' },
                  'VOD_SERVERS': { category: 'source', type: 'text', description: 'VOD 站点配置' },
                  'ADMIN_TOKEN': { category: 'api', type: 'text', description: '系统管理访问令牌' },
                };
                this.get('TEST_SECRET_VALUE', '', 'string', true);
                this.get('TEST_COUNT', 3, 'number');
                this.get('TEST_MODE', 'fast', 'string');
                this.get('VOD_SERVERS', '默认@https://example.com', 'string');
                this.get('ADMIN_TOKEN', '', 'string', true);
                """, new System.Text.UTF8Encoding(false));
            var configDirectory = Path.Combine(project, "config");
            Directory.CreateDirectory(configDirectory);
            EnvPath = Path.Combine(configDirectory, ".env");
            File.WriteAllText(EnvPath, "TEST_SECRET_VALUE=hidden-secret\nTEST_COUNT=5\nVOD_SERVERS=主站@https://example.com\n", new System.Text.UTF8Encoding(false));
        }

        public string Root { get; }

        public string EnvPath { get; }

        public RecordingDialogService Dialogs { get; } = new();

        public StubWriteGate Gate { get; } = new();

        public StubCoreEnvClient EnvClient { get; } = new();

        public RecordingDiagnostics Diagnostics { get; } = new();

        public StubAdminSessionService AdminSession { get; } = new(
            adminMode: true,
            configured: true,
            sessionToken: "admin-session");

        public ConfigurationPageViewModel CreateViewModel(bool allowAdmin = true)
        {
            Gate.Allow = allowAdmin;
            return new ConfigurationPageViewModel(
                new AppPaths(Root, Path.Combine(_directory.Path, "appdata")),
                new StubSettingsStore(),
                Dialogs,
                Gate,
                EnvClient,
                AdminSession,
                Diagnostics);
        }

        public void Dispose() => _directory.Dispose();
    }

    private sealed class StubCoreEnvClient : ICoreEnvClient
    {
        public bool Succeeded { get; set; } = true;
        public string Diagnostic { get; set; } = "核心已删除环境变量";
        public Action<string>? OnDelete { get; set; }
        public List<(string Host, int Port, string? Token, string? AdminToken, string Key)> Requests { get; } = [];

        public Task<CoreEnvDeleteResult> DeleteAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            string key,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((host, port, token, adminToken, key));
            if (Succeeded)
            {
                OnDelete?.Invoke(key);
                return Task.FromResult(CoreEnvDeleteResult.Success(Diagnostic));
            }

            return Task.FromResult(CoreEnvDeleteResult.Failure(Diagnostic));
        }

        public Task<CoreEnvSetResult> SetAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            string key,
            string value,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Succeeded
                ? CoreEnvSetResult.Success($"核心已写入 {key}")
                : CoreEnvSetResult.Failure(Diagnostic));

        public Task<CoreEnvValueResult> ReadConfigValueAsync(
            string host,
            int port,
            string? token,
            string? adminToken,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Succeeded
                ? CoreEnvValueResult.Success("1")
                : CoreEnvValueResult.Failure(Diagnostic));
    }

    private sealed class StubWriteGate : IAdminWriteGate
    {
        public bool Allow { get; set; } = true;

        public List<string> Actions { get; } = [];

        public Action? NavigateToSecurity { get; set; }

        public Task<bool> EnsureAsync(string action, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.FromResult(Allow);
        }
    }

    private sealed class StubSettingsStore : ISettingsStore
    {
        public IReadOnlyDictionary<string, string> Read() =>
            new Dictionary<string, string>(StringComparer.Ordinal);

        public void Write(IReadOnlyDictionary<string, string?> changes) =>
            throw new NotSupportedException("配置页测试不应写入桌面设置");
    }

    private sealed class RecordingDiagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }

        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
}
