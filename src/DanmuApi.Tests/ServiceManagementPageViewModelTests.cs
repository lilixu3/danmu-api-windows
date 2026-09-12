using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.Platform;
using DanmuApi.Runtime;

namespace DanmuApi.Tests;

public sealed class ServiceManagementPageViewModelTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task WritesRequireAdminAndConfirmation(bool authorized, bool confirmed)
    {
        using var directory = new TemporaryDirectory();
        var context = new RuntimeApiContext(new AppPaths(directory.Path, directory.Path), new Runtime(),
            new StubAdminSessionService(true, true, "admin-secret"));
        var client = new Client();
        var dialogs = new RecordingDialogService { Confirmation = confirmed };
        await using var model = new ServiceManagementPageViewModel(context, client, new Gate(authorized), dialogs);
        await model.RefreshCommand.ExecuteAsync(null);
        model.BlacklistText = "192.168.1.20";
        model.BlacklistEnabled = true;
        await model.SaveCommand.ExecuteAsync(null);
        await model.ClearLogsCommand.ExecuteAsync(null);
        Assert.Equal(authorized && confirmed ? 1 : 0, client.Saves);
        Assert.Equal(authorized && confirmed ? 1 : 0, client.Clears);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task FailedWriteInvalidatesSnapshotAndExposesRedactedFailure()
    {
        using var directory = new TemporaryDirectory();
        var context = new RuntimeApiContext(new AppPaths(directory.Path, directory.Path), new Runtime(),
            new StubAdminSessionService(true, true, "admin-secret"));
        var client = new Client { Fail = true };
        await using var model = new ServiceManagementPageViewModel(context, client, new Gate(true),
            new RecordingDialogService { Confirmation = true });
        await model.RefreshCommand.ExecuteAsync(null);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.False(model.HasSnapshot);
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.IsBusy);
        Assert.Contains("失败", model.Diagnostic);
        Assert.DoesNotContain("admin-secret", model.Diagnostic);
    }

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(false, 1200, 800)]
    [InlineData(true, 1200, 800)]
    [InlineData(false, 720, 900)]
    [InlineData(true, 720, 900)]
    public async Task ToolsAndSettingsNavigationRendersMigratedBackup(bool dark, int width, int height)
    {
        using var directory = new TemporaryDirectory();
        var context = new RuntimeApiContext(new AppPaths(directory.Path, directory.Path), new Runtime(), new StubAdminSessionService());
        var dialogs = new RecordingDialogService();
        using var http = new HttpClient();
        var api = new DanmuApiClient(http);
        var diagnostics = new Diagnostics();
        var downloadHandler = new PendingDownloadHandler();
        using var remote = new DanmuApi.Core.BackupWebDavClient(new HttpClient(downloadHandler));
        await using var tools = new ToolsPageViewModel(() => new DanmuTestPageViewModel(context, api, dialogs, diagnostics),
            () => new ApiDebugPageViewModel(context, api, dialogs, diagnostics),
            () => new ServiceManagementPageViewModel(context, new Client(), new Gate(false), dialogs),
            () => new RequestRecordsPageViewModel(new AppPaths(directory.Path, directory.Path), new RequestClient(),
                new LocalRequestRecordStore(), new Runtime(), dialogs, diagnostics));
        var view = new DanmuApi.App.Views.ToolsView { DataContext = tools };
        var window = new Avalonia.Controls.Window { Width = width, Height = height, Content = view, RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light };
        window.Show();
        try
        {
            Assert.Equal(4, tools.SectionOptions.Count);
            Capture(window, $"tools-{dark}-{width}");
            var danmu = Assert.IsType<DanmuTestPageViewModel>(tools.CurrentSection);
            var tabs = Assert.Single(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view)
                .OfType<Avalonia.Controls.ListBox>(), x => x.Name == "DanmuTabSelector");
            Assert.Equal(3, tabs.ItemCount);
            for (var index = 0; index < 3; index++)
            {
                var item = Assert.IsType<Avalonia.Controls.ListBoxItem>(tabs.ContainerFromIndex(index));
                Assert.True(item.IsEffectivelyVisible);
                var position = Avalonia.VisualExtensions.TranslatePoint(item, default, tabs)!.Value;
                Assert.InRange(position.X, 0, tabs.Bounds.Width);
                Assert.True(position.X + item.Bounds.Width <= tabs.Bounds.Width + 1,
                    $"Tab {index} right edge {position.X + item.Bounds.Width} exceeds selector width {tabs.Bounds.Width}");
                tabs.SelectedIndex = index;
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Assert.Same(danmu.TabOptions[index], danmu.SelectedTabOption);
            }
            tools.SelectedSectionOption = tools.SectionOptions.Single(x => x.Value == ToolsSection.ApiDebug);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view), x => x is DanmuApi.App.Views.ApiDebugView);
            Capture(window, $"tools-apidebug-{dark}-{width}");
            tools.SelectedSectionOption = tools.SectionOptions.Single(x => x.Value == ToolsSection.RequestRecords);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var requests = Assert.IsType<RequestRecordsPageViewModel>(tools.CurrentSection);
            Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view), x => x is DanmuApi.App.Views.RequestRecordsView);
            Capture(window, $"tools-requests-{dark}-{width}");
            tools.SelectedSectionOption = tools.SectionOptions.Single(x => x.Value == ToolsSection.ServiceManagement);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view), x => x is DanmuApi.App.Views.ServiceManagementView);
            Capture(window, $"tools-management-{dark}-{width}");
            Assert.Throws<ObjectDisposedException>(() => requests.Start());

        }
        finally { window.Close(); }

        var store = new SettingsStore(Path.Combine(directory.Path, "settings.properties"));
        store.Write(new Dictionary<string, string?> { ["close_action"] = "tray", ["unrelated"] = "keep" });
        var created = 0;
        await using var settings = new SettingsPageViewModel(store, new Autostart(), new Notifications(),
            new AppPaths(directory.Path, directory.Path), backupFactory: () =>
            {
                created++;
                return new BackupPageViewModel(new BackupLocalService(Path.Combine(directory.Path, "config", ".env"), "test", new RestoreGuard()),
                    remote, new BackupWebDavSettings(new ProtectedStore()), new BackupDialogs());
            });
        Assert.Equal(0, created);
        settings.SelectedCategory = settings.Categories.Single(x => x.Key == "backup");
        var firstBackup = Assert.IsType<BackupPageViewModel>(settings.Backup);
        firstBackup.Password = "transient-secret";
        var settingsView = new DanmuApi.App.Views.SettingsView { DataContext = settings };
        var settingsWindow = new Avalonia.Controls.Window { Width = width, Height = height, Content = settingsView,
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light };
        settingsWindow.Show();
        try
        {
            Capture(settingsWindow, $"settings-backup-{dark}-{width}");
            Assert.Contains(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(settingsView), x => x is DanmuApi.App.Views.BackupView);
            firstBackup.CollectionUrl = "https://example.invalid/dav/";
            firstBackup.Username = "fixture-user";
            var download = firstBackup.DownloadCommand.ExecuteAsync(null);
            await downloadHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            settings.SelectedCategory = settings.Categories[0];
            await download.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsAssignableFrom<OperationCanceledException>(firstBackup.LastFailure);
            Assert.False(firstBackup.IsBusy);
            Assert.Null(settings.Backup);
            Assert.Empty(firstBackup.Password);
            Capture(settingsWindow, $"settings-general-{dark}-{width}");
            settings.SelectedCategory = settings.Categories.Single(x => x.Key == "backup");
            Assert.NotSame(firstBackup, settings.Backup);
            var secondBackup = settings.Backup!;
            secondBackup.Password = "another-secret";
            await settings.DisposeAsync();
            Assert.Null(settings.Backup);
            Assert.Empty(secondBackup.Password);
            Assert.Equal("tray", store.Read()["close_action"]);
            Assert.Equal("keep", store.Read()["unrelated"]);
        }
        finally { settingsWindow.Close(); }
    }

    private static void Capture(Avalonia.Controls.Window window, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var directory = Environment.GetEnvironmentVariable("DANMU_TEST_RENDER_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"));
    }
    private sealed class PendingDownloadHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Pending download must end through cancellation");
        }
    }
    private sealed class Autostart : IAutostartService
    {
        public AutostartStatus GetStatus() => new(false, false, "test");
        public Task<AutostartOperationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public AutostartOperationResult RefreshIfEnabled() => throw new NotSupportedException();
    }
    private sealed class Notifications : IDesktopNotificationService
    {
        public Task<DesktopNotificationResult> ShowAsync(string title, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class RequestClient : ICoreRequestRecordsClient
    {
        public Task<CoreRequestRecordsReadResult> ReadAsync(string host, int port, string? token, CancellationToken cancellationToken = default) =>
            Task.FromResult(CoreRequestRecordsReadResult.Success([], 0));
    }
    private sealed class Diagnostics : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) => LastDiagnostic = message;
    }
    private sealed class ProtectedStore : IProtectedStringStore
    {
        public string? Load() => null;
        public void Save(string value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }
    private sealed class RestoreGuard : IBackupRestoreGuard
    {
        public ValueTask<IAsyncDisposable> AcquireStoppedLeaseAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class BackupDialogs : IBackupDialogService
    {
        public Task<string?> PickFileAsync(bool save) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string message) => throw new NotSupportedException();
    }

    private sealed class Gate(bool authorized) : IAdminWriteGate
    {
        public Action? NavigateToSecurity { get; set; }
        public Task<bool> EnsureAsync(string action, CancellationToken cancellationToken = default) => Task.FromResult(authorized);
    }
    private sealed class Client : IRuntimeManagementClient
    {
        public int Saves;
        public int Clears;
        public bool Fail;
        public Task<AccessControlSnapshot> ReadAccessAsync(string host, int port, string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessControlSnapshot("off", [], [], 0, 0));
        public Task<AccessControlSnapshot> SaveAccessAsync(string host, int port, string token, string mode, IReadOnlyList<string> blacklist, bool clearDevices = false, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (Fail) throw new IOException("write failed admin-secret");
            return Task.FromResult(new AccessControlSnapshot(mode, blacklist, [], 0, 0));
        }
        public Task ClearLogsAsync(string host, int port, string token, string adminToken, CancellationToken cancellationToken = default)
        {
            Assert.Equal("admin-secret", adminToken);
            Clears++;
            return Task.CompletedTask;
        }
    }
    private sealed class Runtime : IRuntimeController
    {
        public RuntimeSnapshot Snapshot => new(DesktopRuntimeState.Running, 9321, 123, "test");
        public event EventHandler<RuntimeSnapshot>? SnapshotChanged { add { } remove { } }
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AdoptionResult> AdoptAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public string? ReconcileLiveness() => null;
    }
}
