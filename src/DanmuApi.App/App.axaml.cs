using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core;
using DanmuApi.Platform;
using DanmuApi.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace DanmuApi.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private AppInstanceLock? _instanceLock;
    private TrayService? _trayService;
    private IAppDiagnostics? _diagnostics;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var arguments = desktop.Args ?? [];
            var isAutostart = AutostartManager.IsAutostartLaunch(arguments);
            var requestedCommand = ParseInstanceCommand(arguments);

            AppPaths paths;
            try
            {
                paths = CreateConfiguredPaths();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                Console.Error.WriteLine($"读取运行目录设置失败: {error.Message}");
                StartupExit.Request(desktop, 1);
                base.OnFrameworkInitializationCompleted();
                return;
            }
            RegisterGlobalExceptionLogging(paths);
            _instanceLock = new AppInstanceLock(paths);
            var lockResult = _instanceLock.TryAcquireDetailed();
            if (!lockResult.Succeeded)
            {
                if (lockResult.AlreadyOwned)
                {
                    var wakeResult = _instanceLock.SendCommand(requestedCommand);
                    if (!wakeResult.Succeeded && !isAutostart)
                    {
                        Console.Error.WriteLine($"已有弹幕 API 实例，但唤醒失败: {wakeResult.Diagnostic}");
                    }
                }
                else
                {
                    Console.Error.WriteLine(lockResult.Diagnostic);
                }

                StartupExit.Request(desktop, 1);
                base.OnFrameworkInitializationCompleted();
                return;
            }

            InitializePreparedDesktop(desktop, paths, isAutostart, requestedCommand);
        }
        base.OnFrameworkInitializationCompleted();
    }

            // 可选 Redis 依赖不在随包受管清单里（上游只在配置 LOCAL_REDIS_URL 时才 import 它），
            // 因此单独按配置铺开；结果只用于提醒，不改变运行环境就绪状态。
    private static void RegisterGlobalExceptionLogging(AppPaths paths)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            StartupTiming.RecordFailure(paths, args.ExceptionObject as Exception
                ?? new InvalidOperationException(args.ExceptionObject?.ToString() ?? "未提供未处理异常对象"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            StartupTiming.RecordFailure(paths, args.Exception);
            args.SetObserved();
        };
    }

    private void InitializePreparedDesktop(IClassicDesktopStyleApplicationLifetime desktop, AppPaths paths, bool isAutostart, InstanceCommand requestedCommand)
    {
            MainWindow? mainWindow = null;
            _services = ConfigureServices(paths, desktop, () => mainWindow).BuildServiceProvider();
            _diagnostics = _services.GetRequiredService<IAppDiagnostics>();
            _services.GetRequiredService<ThemeService>().Initialize();
            var autostart = _services.GetRequiredService<IAutostartService>();
            var refreshResult = autostart.RefreshIfEnabled();
            if (!refreshResult.Succeeded)
            {
                _diagnostics.Record($"启动时刷新开机自启失败: {refreshResult.Diagnostic}");
            }

            var viewModel = _services.GetRequiredService<MainWindowViewModel>();
            mainWindow = new MainWindow { DataContext = viewModel, Diagnostics = _diagnostics };
            mainWindow.Opened += (_, _) => StartupTiming.Record(paths, "主窗口显示", System.Diagnostics.Stopwatch.GetElapsedTime(Program.StartTimestamp));
            if (requestedCommand == InstanceCommand.SHOW_APP_UPDATE && !isAutostart)
            {
                viewModel.NavigateTo("about");
            }
            else if (requestedCommand == InstanceCommand.SHOW_SETTINGS && !isAutostart)
            {
                viewModel.NavigateTo("settings");
            }
            else if (requestedCommand == InstanceCommand.APPLY_CORE_UPDATE && !isAutostart)
            {
                viewModel.NavigateTo("core");
            }

            _lifecycleCoordinator = _services.GetRequiredService<AppLifecycleCoordinator>();
            var applicationUpdates = _services.GetRequiredService<ApplicationUpdateViewModel>();
            viewModel.ApplicationUpdates = applicationUpdates;
            viewModel.SettingsPage.ApplicationUpdates = applicationUpdates;
            applicationUpdates.ShowUpdatePage = () => viewModel.NavigateTo("about");
            // 工具页里的页面（本地弹幕的「核心配置」）在构造期拿不到外壳，这里补上跳转入口。
            _services.GetRequiredService<ShellNavigationAccessor>().NavigateTo = viewModel.NavigateTo;
            applicationUpdates.UpdateBlockedReason = () => viewModel.HasActiveDownload || viewModel.CorePage?.IsBusy == true || _services.GetRequiredService<IPendingCoreUpdateService>().IsApplying
                ? "请先完成或暂停弹幕下载及核心安装/更新，再进行软件更新。" : null;
            applicationUpdates.IsServiceRunningBeforeUpdate = () => viewModel.IsServiceRunning;
            applicationUpdates.ExitForUpdate = () => _lifecycleCoordinator.TryExitForUpdateAsync();
            applicationUpdates.Start();
            mainWindow.AttachLifecycle(_lifecycleCoordinator);
            if (!isAutostart)
            {
                desktop.MainWindow = mainWindow;
            }

            var controlServerResult = (_instanceLock ?? throw new InvalidOperationException("启动时单实例锁未建立")).StartControlServer(command =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        switch (command)
                        {
                            case InstanceCommand.SHOW_APP_UPDATE:
                                viewModel.NavigateTo("about");
                                _lifecycleCoordinator.RestoreMainWindow();
                                break;
                            case InstanceCommand.SHOW_SETTINGS:
                                viewModel.NavigateTo("settings");
                                _lifecycleCoordinator.RestoreMainWindow();
                                break;
                            case InstanceCommand.APPLY_CORE_UPDATE:
                                _ = ApplyPendingUpdateAsync(_services, _diagnostics);
                                break;
                            default:
                                viewModel.NavigateTo("overview");
                                _lifecycleCoordinator.RestoreMainWindow();
                                break;
                        }
                    }
                    catch (Exception error)
                    {
                        _diagnostics.Record("处理单实例唤醒命令失败", error);
                    }
                });
            });
            if (!controlServerResult.Succeeded)
            {
                _diagnostics.Record($"本地唤醒通道启动失败: {controlServerResult.Diagnostic}");
            }

            _trayService = _services.GetRequiredService<TrayService>();
            var updateScheduler = _services.GetRequiredService<ICoreUpdateScheduler>();
            updateScheduler.DiagnosticChanged += (_, diagnostic) => _diagnostics.Record(diagnostic);
            updateScheduler.Start();
            var notifications = _services.GetRequiredService<IDesktopNotificationService>();
            {
                async void BeginPreparation()
                {
                    try
                    {
                        await _services.GetRequiredService<RuntimePreparationService>().PrepareAsync().ConfigureAwait(true);
                        if (_services.GetRequiredService<RuntimePreparationService>().StartBlockedReason is { } blocked)
                        {
                            _diagnostics.Record(blocked);
                            desktop.MainWindow = mainWindow;
                            mainWindow.Show();
                            return;
                        }
                        ApplicationUpdateHelper.AcknowledgeStartup(desktop.Args ?? []);
                        if (isAutostart)
                        {
                            mainWindow.Hide();
                            updateScheduler.SetBackgroundActive(true);
                            var controller = _services.GetRequiredService<IRuntimeController>();
                            await controller.StartAsync().ConfigureAwait(true);
                            if (controller.Snapshot.State == DesktopRuntimeState.Running)
                            {
                                var notification = await notifications.ShowAsync(
                                    DesktopNotificationKind.StartupSucceeded,
                                    "弹幕 API",
                                    $"服务已在后台启动，端口 {controller.Snapshot.Port}").ConfigureAwait(true);
                                if (notification.Status == DesktopNotificationStatus.Failed)
                                {
                                    _diagnostics.Record($"自启成功通知失败: {notification.Diagnostic}");
                                }
                                await updateScheduler.CheckBackgroundAsync().ConfigureAwait(true);
                            }
                            else
                            {
                                _diagnostics.Record($"--autostart 未启动：{controller.Snapshot.State}；{controller.Snapshot.FailureReason ?? "未提供失败原因"}");
                                var exited = await _lifecycleCoordinator.TryExitApplicationAsync().ConfigureAwait(true);
                                if (!exited)
                                {
                                    _diagnostics.Record("--autostart 清理失败，主窗口保持隐藏；请通过托盘查看诊断");
                                }
                            }
                        }
                        else
                        {
                            await _services.GetRequiredService<IRuntimeController>()
                                .AdoptAsync()
                                .ConfigureAwait(true);
                            if (ApplicationUpdateHelper.ShouldResumeService(desktop.Args ?? []))
                            {
                                var jobDirectory = ApplicationUpdateHelper.ValidateJobDirectory(desktop.Args![1]);
                                await UpdatedServiceRestorer.RestoreAsync(true, _services.GetRequiredService<IRuntimeController>(),
                                    Path.Combine(jobDirectory, "service-restore.txt"), _diagnostics, _services.GetRequiredService<IUiDialogService>());
                            }
                            if (requestedCommand == InstanceCommand.APPLY_CORE_UPDATE)
                            {
                                await ApplyPendingUpdateAsync(_services, _diagnostics).ConfigureAwait(true);
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        _diagnostics.Record(
                            isAutostart ? "--autostart 后台启动服务失败" : "启动时认领已有 Node 失败",
                            error);
                    }
                }
                if (isAutostart) Dispatcher.UIThread.Post(BeginPreparation, DispatcherPriority.Background);
                else mainWindow.Opened += (_, _) => mainWindow.RequestAnimationFrame(_ =>
                {
                    StartupTiming.Record(paths, "主窗口首帧准备门控", System.Diagnostics.Stopwatch.GetElapsedTime(Program.StartTimestamp));
                    Dispatcher.UIThread.Post(BeginPreparation, DispatcherPriority.Background);
                });
            }
            desktop.Exit += (_, _) => CleanupDesktop();
    }

    private AppLifecycleCoordinator? _lifecycleCoordinator;

    // internal 而非 private：Di 组合根必须能被测试直接解析，否则新增注册写错依赖
    // 只会在用户机器上首次启动时才暴露。
    internal static IServiceCollection ConfigureServices(
        AppPaths paths,
        IClassicDesktopStyleApplicationLifetime desktop,
        Func<MainWindow?> ownerProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton<IAppDiagnostics, AppDiagnostics>();
        services.AddSingleton<ISettingsStore>(provider =>
            new SettingsStore(provider.GetRequiredService<AppPaths>().SettingsFile));
        services.AddSingleton<ThemeService>();
        services.AddSingleton(provider => new RuntimePreparationService((forceRepair, progress, cancellationToken) => Task.Run(() =>
        {
            using var timing = StartupTiming.Measure(paths, "后台运行环境准备");
            var result = BundledRuntimePreparer.PrepareForStartup(Path.Combine(AppContext.BaseDirectory, "runtime-bundle"), paths,
                forceRepair: forceRepair, progress: progress, cancellationToken: cancellationToken);
            StartupTiming.Record(paths, $"依赖完成 sourceHashes={result.SourceFilesHashed} targetHashes={result.TargetFilesHashed}", result.Elapsed);
            // 可选 Redis 依赖不在随包受管清单里（上游只在配置 LOCAL_REDIS_URL 时才 import 它），
            // 因此单独按配置铺开；结果只用于提醒，不改变运行环境就绪状态。
            var preparation = provider.GetRequiredService<RuntimePreparationService>();
            var redis = OptionalRedisStager.Stage(Path.Combine(AppContext.BaseDirectory, "runtime-bundle"), paths, cancellationToken);
            preparation.ReportOptionalRedis(redis);
            if (redis.Diagnostic is { } redisDiagnostic)
            {
                // 配置了 LOCAL_REDIS_URL 却铺不开，必须留下诊断；状态带也会显示"铺开失败"。
                provider.GetRequiredService<IAppDiagnostics>().Record(redisDiagnostic, new IOException(redisDiagnostic));
            }
        }, cancellationToken)));
        services.AddSingleton<IGithubTokenStore>(provider =>
            new WindowsGithubTokenStore(provider.GetRequiredService<AppPaths>().GithubTokenFile));
        services.AddSingleton<IGithubTokenProvider>(provider => provider.GetRequiredService<IGithubTokenStore>());
        services.AddSingleton<IProtectedStringStore>(provider => new WindowsProtectedStringStore(
            provider.GetRequiredService<AppPaths>().AdminSessionFile,
            "DanmuApi.Windows.AdminSession.v1"));
        services.AddSingleton<IAdminSessionService>(provider => new AdminSessionService(
            provider.GetRequiredService<IProtectedStringStore>(),
            () => Path.Combine(
                provider.GetRequiredService<AppPaths>().NodeProjectDirectory,
                "config",
                ".env")));
        services.AddSingleton<IAdminWriteGate, AdminWriteGate>();
        services.AddSingleton<GithubHttpClientResources>();
        services.AddSingleton<IGithubCoreRemote>(provider => new GithubCoreRemote(
            provider.GetRequiredService<GithubHttpClientResources>().Api,
            provider.GetRequiredService<GithubHttpClientResources>().Download,
            provider.GetRequiredService<IGithubTokenProvider>(),
            provider.GetRequiredService<IGithubRoutePreferenceStore>()));
        services.AddSingleton<IGithubTokenConfigurationService, GithubTokenConfigurationService>();
        services.AddSingleton<IGithubFileDownloader>(provider => new GithubFileDownloader(
            provider.GetRequiredService<GithubHttpClientResources>().Download,
            routePreferences: provider.GetRequiredService<IGithubRoutePreferenceStore>()));
        services.AddSingleton<ICoreInstaller>(provider => new CoreInstaller(
            provider.GetRequiredService<AppPaths>().NodeProjectDirectory,
            provider.GetRequiredService<AppPaths>().CoreCacheDirectory,
            provider.GetRequiredService<IGithubFileDownloader>()));
        services.AddSingleton<ICoreUpdateTimestampStore, SettingsCoreUpdateTimestampStore>();
        services.AddSingleton<ICoreUpdatePolicyStore, SettingsCoreUpdatePolicyStore>();
        services.AddSingleton<IGithubRoutePreferenceStore, SettingsGithubRoutePreferenceStore>();
        services.AddSingleton<IGithubProxySpeedTester>(provider => new GithubProxySpeedTester(
            provider.GetRequiredService<GithubHttpClientResources>().Download));
        services.AddSingleton<IPlatformCommandExecutor, ProcessCommandExecutor>();
        services.AddSingleton<IRuntimeFirewall, FirewallManager>();
        services.AddSingleton<AutostartManager>();
        services.AddSingleton<IAutostartService, PlatformAutostartService>();
        services.AddSingleton<NativeToastNotificationService>();
        services.AddSingleton<IDesktopNotificationService>(provider => new PreferenceDesktopNotificationService(
            provider.GetRequiredService<NativeToastNotificationService>(), provider.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<ApplicationUpdateViewModel>();
        services.AddSingleton<IBackupRestoreGuard, BackupRestoreGuard>();
        services.AddSingleton(provider => new BackupLocalService(Path.Combine(paths.NodeProjectDirectory, "config", ".env"),
            typeof(App).Assembly.GetName().Version!.ToString(3), provider.GetRequiredService<IBackupRestoreGuard>(), paths.SettingsFile));
        services.AddSingleton(_ => new BackupWebDavClient(BackupWebDavClient.CreateHttpClient()));
        services.AddSingleton(_ => new BackupWebDavSettings(new WindowsProtectedStringStore(
            Path.Combine(paths.SettingsDirectory, "webdav-backup.dat"), "DanmuApi.Windows.WebDavBackup")));
        services.AddSingleton<IBackupDialogService>(_ => new BackupDialogService(() => ownerProvider() ?? throw new InvalidOperationException("主窗口尚未初始化")));
        services.AddTransient<BackupPageViewModel>();
        services.AddSingleton<Func<BackupPageViewModel>>(provider => () => provider.GetRequiredService<BackupPageViewModel>());
        services.AddSingleton<IProcessTerminator, WindowsProcessTerminator>();
        services.AddSingleton<IRuntimeHealthClient, RuntimeHealthClient>();
        services.AddSingleton<ICoreLogClient, CoreLogClient>();
        services.AddSingleton<IRuntimeManagementClient, RuntimeManagementClient>();
        services.AddTransient<ServiceManagementPageViewModel>();
        services.AddSingleton<Func<ServiceManagementPageViewModel>>(provider => () => provider.GetRequiredService<ServiceManagementPageViewModel>());
        services.AddSingleton<ICoreRequestRecordsClient, CoreRequestRecordsClient>();
        services.AddSingleton<ILocalRequestRecordStore, LocalRequestRecordStore>();
        services.AddSingleton<ICoreCacheClient, CoreCacheClient>();
        services.AddSingleton<ICoreEnvClient, CoreEnvClient>();
        services.AddSingleton(provider => new PosterImageService(
            diagnostics: provider.GetRequiredService<IAppDiagnostics>()));
        services.AddSingleton<DanmuDownloadStore>(provider => new DanmuDownloadStore(provider.GetRequiredService<AppPaths>().SettingsDirectory));
        services.AddSingleton<DanmuDownloadFileService>(provider => new DanmuDownloadFileService(
            provider.GetRequiredService<DanmuDownloadStore>(),
            () => provider.GetRequiredService<IDanmuApiClient>()));
        services.AddSingleton<ICoreCredentialClient, CoreCredentialClient>();
        services.AddSingleton<RuntimeApiContext>();
        services.AddSingleton<IDanmuApiClient>(provider => new DanmuApiClient(
            runtimeController: provider.GetRequiredService<IRuntimeController>(),
            requestRecords: provider.GetRequiredService<ILocalRequestRecordStore>()));
        services.AddSingleton<IUiDialogService>(provider => new UiDialogService(
            ownerProvider,
            provider.GetRequiredService<AppPaths>(),
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IAdminSessionService>(),
            provider.GetRequiredService<IRuntimeController>(),
            provider.GetRequiredService<ICoreCredentialClient>()));
        services.AddSingleton<INodeSupervisor>(provider => new NodeSupervisor(
            provider.GetRequiredService<IRuntimeHealthClient>(),
            provider.GetRequiredService<IProcessTerminator>()));
        services.AddSingleton<IRuntimeController>(provider =>
        {
            var settingsStore = provider.GetRequiredService<ISettingsStore>();
            return new RuntimeController(
                provider.GetRequiredService<INodeSupervisor>(),
                () =>
                {
                    var config = DesktopConfigReader.Read(settingsStore, paths.NodeProjectDirectory);
                    return new StartConfig(
                        NodeExe: Path.Combine(paths.RuntimeDirectory, "node.exe"),
                        ScriptDir: paths.NodeProjectDirectory,
                        Port: config.Port,
                        ListenHost: config.ListenHost,
                        Variant: config.Variant,
                        IdentityFile: paths.IdentityFile,
                        // 首次启动核心要建编译缓存、初始化数据源，可能触发 Windows 防火墙
                        // 授权弹窗；30 秒会在用户还没确认网络时就报健康检查超时。
                        StartupTimeout: TimeSpan.FromSeconds(90));
                },
                provider.GetRequiredService<IRuntimeFirewall>(),
                () => provider.GetRequiredService<RuntimePreparationService>().StartBlockedReason);
        });
        services.AddSingleton<ICoreManagementService>(provider => new CoreManagementService(
            provider.GetRequiredService<ICoreInstaller>(),
            provider.GetRequiredService<IGithubCoreRemote>(),
            provider.GetRequiredService<IRuntimeController>(),
            () => ReadManagedVariant(
                provider.GetRequiredService<ISettingsStore>(),
                provider.GetRequiredService<AppPaths>()),
            provider.GetRequiredService<RuntimePreparationService>().AcquireReadyLeaseAsync));
        services.AddSingleton<ICoreUpdateCoordinator, CoreUpdateCoordinator>();
        services.AddSingleton<CoreUpdateResultHandler>(provider => new CoreUpdateResultHandler(
            provider.GetRequiredService<ICoreManagementService>(),
            provider.GetRequiredService<IGithubRoutePreferenceStore>(),
            provider.GetRequiredService<IDesktopNotificationService>(),
            provider.GetRequiredService<IAppDiagnostics>()));
        services.AddSingleton<ICoreUpdateResultHandler>(provider =>
            provider.GetRequiredService<CoreUpdateResultHandler>());
        services.AddSingleton<IPendingCoreUpdateService>(provider =>
            provider.GetRequiredService<CoreUpdateResultHandler>());
        services.AddSingleton<ICoreUpdateScheduler>(provider => new CoreUpdateScheduler(
            provider.GetRequiredService<ICoreUpdateCoordinator>(),
            provider.GetRequiredService<ICoreUpdatePolicyStore>(),
            provider.GetRequiredService<ICoreUpdateResultHandler>(),
            () => ReadManagedVariant(
                provider.GetRequiredService<ISettingsStore>(),
                provider.GetRequiredService<AppPaths>())));
        services.AddSingleton<SettingsPageViewModel>(provider => new SettingsPageViewModel(
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IAutostartService>(),
            provider.GetRequiredService<IDesktopNotificationService>(),
            provider.GetRequiredService<AppPaths>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<ICoreUpdatePolicyStore>(),
            provider.GetRequiredService<ICoreUpdateScheduler>(),
            provider.GetRequiredService<IGithubRoutePreferenceStore>(),
            provider.GetRequiredService<IGithubProxySpeedTester>(),
            provider.GetRequiredService<IAdminSessionService>(),
            provider.GetRequiredService<IGithubTokenConfigurationService>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            provider.GetRequiredService<Func<BackupPageViewModel>>(),
            themeService: provider.GetRequiredService<ThemeService>()));
        services.AddSingleton<RuntimeMaintenanceService>(provider => new RuntimeMaintenanceService(
            Path.Combine(AppContext.BaseDirectory, "runtime-bundle"),
            provider.GetRequiredService<AppPaths>(),
            provider.GetRequiredService<IRuntimeController>(),
            provider.GetRequiredService<ICoreUpdateScheduler>(),
            provider.GetRequiredService<IPendingCoreUpdateService>(),
            provider.GetRequiredService<RuntimePreparationService>()));
        services.AddSingleton<IRuntimeMaintenanceService>(provider => provider.GetRequiredService<RuntimeMaintenanceService>());
        services.AddSingleton<RuntimeMaintenanceViewModel>();
        services.AddSingleton<ICoreDependencyService>(provider =>
        {
            var paths = provider.GetRequiredService<AppPaths>();
            var maintenance = provider.GetRequiredService<RuntimeMaintenanceService>();
            var installer = provider.GetRequiredService<ICoreInstaller>() as CoreInstaller
                ?? throw new InvalidOperationException("核心安装器不支持依赖维护租约。");
            return new CoreDependencyService(paths.NodeProjectDirectory, Path.Combine(paths.RuntimeDirectory, "node.exe"),
                maintenance.AcquireStoppedLeaseAsync, installer.AcquireMaintenanceLeaseAsync,
                notBundledDependencies: () =>
                {
                    // redis 只有真正配置了（并已铺开）才算核心必需；没配置时它属于按需可选，
                    // 不该算进缺失。配置了但铺开失败则照报——那种情况确实会缺。
                    var redisExpected = provider.GetRequiredService<RuntimePreparationService>().OptionalRedis
                        is { Configured: true };
                    return CoreDependencyService.NotBundledDependencyNames(redisExpected);
                });
        });
        services.AddSingleton<CoreDependencyVerifier>(provider => new CoreDependencyVerifier(
            provider.GetRequiredService<ICoreDependencyService>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IDesktopNotificationService>(),
            provider.GetRequiredService<IAppDiagnostics>()));
        services.AddSingleton<CorePageViewModel>(provider => new CorePageViewModel(
            provider.GetRequiredService<ICoreManagementService>(),
            provider.GetRequiredService<IGithubCoreRemote>(),
            provider.GetRequiredService<IGithubRoutePreferenceStore>(),
            provider.GetRequiredService<IGithubProxySpeedTester>(),
            provider.GetRequiredService<ICoreUpdateScheduler>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            provider.GetRequiredService<IGithubTokenConfigurationService>(),
            provider.GetRequiredService<CoreDependencyVerifier>().VerifyAsync,
            provider.GetRequiredService<RuntimePreparationService>()));
        services.AddSingleton<ConfigurationPageViewModel>(provider => new ConfigurationPageViewModel(
            provider.GetRequiredService<AppPaths>(),
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAdminWriteGate>(),
            provider.GetRequiredService<ICoreEnvClient>(),
            provider.GetRequiredService<IAdminSessionService>(),
            provider.GetRequiredService<IAppDiagnostics>()));
        services.AddTransient<LogsPageViewModel>(provider => new LogsPageViewModel(
            provider.GetRequiredService<AppPaths>(),
            new LogFileTailReader(),
            provider.GetRequiredService<ICoreLogClient>(),
            provider.GetRequiredService<IRuntimeController>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAppDiagnostics>()));
        services.AddSingleton<Func<ActivityPageViewModel>>(provider => () => new ActivityPageViewModel(
            provider.GetRequiredService<LogsPageViewModel>()));
        services.AddSingleton<Func<RequestRecordsPageViewModel>>(provider => () => new RequestRecordsPageViewModel(
                provider.GetRequiredService<AppPaths>(),
                provider.GetRequiredService<ICoreRequestRecordsClient>(),
                provider.GetRequiredService<ILocalRequestRecordStore>(),
                provider.GetRequiredService<IRuntimeController>(),
                provider.GetRequiredService<IUiDialogService>(),
                provider.GetRequiredService<IAppDiagnostics>()));
        services.AddSingleton<Func<DanmuTestPageViewModel>>(provider => () => new DanmuTestPageViewModel(
            provider.GetRequiredService<RuntimeApiContext>(),
            provider.GetRequiredService<IDanmuApiClient>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            provider.GetRequiredService<ILocalRequestRecordStore>(),
            provider.GetRequiredService<PosterImageService>()));
        services.AddSingleton<Func<ApiDebugPageViewModel>>(provider => () => new ApiDebugPageViewModel(
            provider.GetRequiredService<RuntimeApiContext>(),
            provider.GetRequiredService<IDanmuApiClient>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAppDiagnostics>()));
        services.AddSingleton<ICoreLocalDanmuClient>(provider => new CoreLocalDanmuClient());
        services.AddSingleton<ILocalDanmuCacheReader>(_ => new LocalDanmuCacheReader());
        services.AddSingleton<ShellNavigationAccessor>();
        services.AddSingleton<Func<LocalDanmuPageViewModel>>(provider => () => new LocalDanmuPageViewModel(
            provider.GetRequiredService<RuntimeApiContext>(),
            provider.GetRequiredService<ICoreLocalDanmuClient>(),
            provider.GetRequiredService<ILocalDanmuCacheReader>(),
            provider.GetRequiredService<IDanmuApiClient>(),
            provider.GetRequiredService<ICoreEnvClient>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAdminWriteGate>(),
            provider.GetRequiredService<IAdminSessionService>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            provider.GetRequiredService<ShellNavigationAccessor>(),
            provider.GetRequiredService<AppPaths>()));
        services.AddSingleton<Func<ToolsPageViewModel>>(provider => () => new ToolsPageViewModel(
            provider.GetRequiredService<Func<DanmuTestPageViewModel>>(),
            provider.GetRequiredService<Func<ApiDebugPageViewModel>>(),
            provider.GetRequiredService<Func<ServiceManagementPageViewModel>>(),
            provider.GetRequiredService<Func<RequestRecordsPageViewModel>>(),
            provider.GetRequiredService<Func<LocalDanmuPageViewModel>>()));
        services.AddSingleton<Func<DanmuDownloadPageViewModel>>(provider => () => new DanmuDownloadPageViewModel(
            provider.GetRequiredService<RuntimeApiContext>(),
            provider.GetRequiredService<IDanmuApiClient>(),
            provider.GetRequiredService<ICoreEnvClient>(),
            provider.GetRequiredService<DanmuDownloadStore>(),
            provider.GetRequiredService<DanmuDownloadFileService>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            provider.GetRequiredService<PosterImageService>()));
        services.AddSingleton<MainWindowViewModel>(provider => new MainWindowViewModel(
            provider.GetRequiredService<IRuntimeController>(),
            provider.GetRequiredService<IRuntimeHealthClient>(),
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<ICoreCacheClient>(),
            provider.GetRequiredService<AppPaths>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<SettingsPageViewModel>(),
            provider.GetRequiredService<IAdminSessionService>(),
            provider.GetRequiredService<CorePageViewModel>(),
            provider.GetRequiredService<Func<ActivityPageViewModel>>(),
            provider.GetRequiredService<Func<ToolsPageViewModel>>(),
            provider.GetRequiredService<Func<DanmuDownloadPageViewModel>>(),
            provider.GetRequiredService<ConfigurationPageViewModel>(),
            provider.GetRequiredService<IAdminWriteGate>(),
            provider.GetRequiredService<ICoreRequestRecordsClient>(),
            provider.GetRequiredService<RuntimePreparationService>(),
            provider.GetRequiredService<ICoreManagementService>()));
        services.AddSingleton<AppLifecycleCoordinator>(provider => new AppLifecycleCoordinator(
            provider.GetRequiredService<IRuntimeController>(),
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            provider.GetRequiredService<ICoreUpdateScheduler>(),
            desktop,
            ownerProvider));
        services.AddSingleton<TrayService>(provider =>
        {
            var viewModel = provider.GetRequiredService<MainWindowViewModel>();
            var lifecycle = provider.GetRequiredService<AppLifecycleCoordinator>();
            return new TrayService(
                provider.GetRequiredService<IRuntimeController>(),
                provider.GetRequiredService<IPendingCoreUpdateService>(),
                provider.GetRequiredService<IAppDiagnostics>(),
                () =>
                {
                    viewModel.NavigateTo("overview");
                    lifecycle.RestoreMainWindow();
                },
                () =>
                {
                    viewModel.NavigateTo("configuration");
                    lifecycle.RestoreMainWindow();
                },
                () =>
                {
                    viewModel.NavigateTo("settings");
                    lifecycle.RestoreMainWindow();
                },
                lifecycle.ExitApplicationAsync);
        });
        return services;
    }

    private static AppPaths CreateConfiguredPaths()
    {
        var defaults = new AppPaths();
        var settings = new SettingsStore(defaults.SettingsFile).Read();
        if (!settings.TryGetValue("runtime_root", out var root) || string.IsNullOrWhiteSpace(root))
        {
            return defaults;
        }

        if (!Path.IsPathFullyQualified(root))
        {
            throw new FormatException("设置 runtime_root 必须是绝对路径");
        }

        return new AppPaths(root.Trim(), defaults.SettingsDirectory);
    }

    private static InstanceCommand ParseInstanceCommand(IReadOnlyList<string> arguments)
    {
        if (arguments.Any(argument => string.Equals(argument.TrimEnd('/'), "danmuapi://update-app", StringComparison.OrdinalIgnoreCase))) return InstanceCommand.SHOW_APP_UPDATE;
        if (arguments.Any(argument => string.Equals(argument, "--settings", StringComparison.Ordinal)))
        {
            return InstanceCommand.SHOW_SETTINGS;
        }

        return arguments.Any(argument =>
            string.Equals(argument.TrimEnd('/'), "danmuapi://update-core", StringComparison.OrdinalIgnoreCase))
            ? InstanceCommand.APPLY_CORE_UPDATE
            : InstanceCommand.SHOW_OVERVIEW;
    }

    private static async Task ApplyPendingUpdateAsync(
        ServiceProvider? services,
        IAppDiagnostics? diagnostics)
    {
        if (services is null)
        {
            diagnostics?.Record("立即更新核心失败：应用服务尚未初始化");
            return;
        }

        try
        {
            var result = await services.GetRequiredService<IPendingCoreUpdateService>()
                .ApplyPendingAsync()
                .ConfigureAwait(true);
            if (result is null)
            {
                diagnostics?.Record("立即更新核心未执行：当前没有待处理更新");
            }
            else if (!result.Succeeded)
            {
                diagnostics?.Record(result.Diagnostic);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            diagnostics?.Record("立即更新核心失败", error);
        }
    }

    private static ManagedCoreVariant ReadManagedVariant(ISettingsStore settingsStore, AppPaths paths)
    {
        var variant = DesktopConfigReader.Read(settingsStore, paths.NodeProjectDirectory).Variant;
        return ManagedCoreVariantExtensions.ParseManagedVariant(variant);
    }

    private void CleanupDesktop()
    {
        try
        {
            _services?.GetRequiredService<RuntimePreparationService>().CancelAndWaitAsync().GetAwaiter().GetResult();
            _trayService?.Dispose();
            _trayService = null;

            _services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _services = null;

            var releaseResult = _instanceLock?.Release();
            if (releaseResult is { Succeeded: false })
            {
                _diagnostics?.Record($"释放单实例锁失败: {releaseResult.Diagnostic}");
            }

            _instanceLock = null;
        }
        catch (Exception error)
        {
            _diagnostics?.Record("应用退出清理失败", error);
        }
    }
}
