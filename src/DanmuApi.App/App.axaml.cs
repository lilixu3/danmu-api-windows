using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DanmuApi.App.Services;
using DanmuApi.App.ViewModels;
using DanmuApi.App.Views;
using DanmuApi.Core;
using DanmuApi.Core.Frp;
using DanmuApi.Platform;
using DanmuApi.Platform.Frp;
using DanmuApi.Runtime;
using DanmuApi.Runtime.Frp;
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
            // 界面线程上的未处理异常会直接终止进程（真机事故：AsyncRelayCommand 把命令里的异常
            // 在 UI 线程重抛，点一下开关应用就没了）。这里统一记诊断并拦截，
            // 让"某个操作出错"表现为一条可查的记录，而不是整个应用消失。
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                _diagnostics?.Record("界面线程未处理异常（已拦截，应用继续运行）", args.Exception);
                args.Handled = true;
            };
            _services.GetRequiredService<ThemeService>().Initialize();
            var autostart = _services.GetRequiredService<IAutostartService>();
            var refreshResult = autostart.RefreshIfEnabled();
            if (!refreshResult.Succeeded)
            {
                _diagnostics.Record($"启动时刷新开机自启失败: {refreshResult.Diagnostic}");
            }

            RestorePersistedCoreDiscovery(_services, paths);

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
            // 进前台（窗口激活、从托盘恢复）时与核心更新一起静默检查软件更新，各自带冷却。
            _lifecycleCoordinator.ApplicationUpdates = applicationUpdates;
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
                            case InstanceCommand.REQUEST_EXIT:
                                // 安装器请本实例退出：走与「退出应用」同一条路径（先停服务再关进程），
                                // 单实例锁与唤醒通道由 CleanupDesktop 释放。调用方只收到「已受理」，
                                // 真正的完成以锁被释放为准（见 RunningInstanceExitRequester）。
                                _ = _lifecycleCoordinator?.ExitApplicationAsync();
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
            // 穿透的状态对账循环与（用户开启的）自启都在这里起步；退出时由容器释放，
            // 释放路径会把 frpc/frps 一并终止，不留孤儿进程。
            _services.GetRequiredService<IFrpTunnelService>().Start();
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
                                var reason = $"{controller.Snapshot.State}；{controller.Snapshot.FailureReason ?? "未提供失败原因"}";
                                _diagnostics.Record($"--autostart 未启动：{reason}");
                                var failureNotification = await notifications.ShowAsync(
                                    DesktopNotificationKind.StartupFailed,
                                    "弹幕 API 自启失败",
                                    $"服务未能在后台启动：{reason}。请打开应用查看原因。").ConfigureAwait(true);
                                if (failureNotification.Status == DesktopNotificationStatus.Failed)
                                {
                                    _diagnostics.Record($"自启失败通知提交失败: {failureNotification.Diagnostic}");
                                }

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
                // 准备只跑一次：主窗口每被打开一次都会走下面这条 Opened 路径（托盘隐藏后再显示也算），
                // 而它包含更新后的启动回执与服务恢复，重复执行会把回执、恢复报告和服务操作再写一遍。
                var preparation = new SingleShotGate();
                void RequestPreparation()
                {
                    if (!preparation.TryEnter()) return;
                    Dispatcher.UIThread.Post(BeginPreparation, DispatcherPriority.Background);
                }
                if (isAutostart) RequestPreparation();
                else mainWindow.Opened += (_, _) => mainWindow.RequestAnimationFrame(_ =>
                {
                    if (preparation.Entered) return;
                    StartupTiming.Record(paths, "主窗口首帧准备门控", System.Diagnostics.Stopwatch.GetElapsedTime(Program.StartTimestamp));
                    RequestPreparation();
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
        services.AddSingleton<IOutboundSettingsStore>(provider => new OutboundSettingsStore(
            provider.GetRequiredService<AppPaths>(),
            message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
        services.AddSingleton<IOutboundDirectService>(provider => new OutboundDirectService(
            provider.GetRequiredService<IOutboundSettingsStore>(),
            provider.GetRequiredService<IRuntimeController>(),
            provider.GetRequiredService<IAppDiagnostics>()));
        services.AddSingleton<OutboundDirectViewModel>();
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
                ".env"),
            diagnosticSink: message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
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
            routePreferences: provider.GetRequiredService<IGithubRoutePreferenceStore>(),
            // 核心 zipball 走 api.github.com，已配置 Token 时必须用上（否则匿名 60/小时 会先耗尽）。
            tokenProvider: provider.GetRequiredService<IGithubTokenProvider>()));
        services.AddSingleton<ICoreInstaller>(provider => new CoreInstaller(
            provider.GetRequiredService<AppPaths>().NodeProjectDirectory,
            provider.GetRequiredService<AppPaths>().CoreCacheDirectory,
            provider.GetRequiredService<IGithubFileDownloader>(),
            null,
            // 核心已装好但善后（历史归档/临时文件清理）失败时，不算安装失败，但必须留痕。
            message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
        services.AddSingleton<ICoreUpdateTimestampStore, SettingsCoreUpdateTimestampStore>();
        services.AddSingleton<ICoreUpdateDiscoveryStore>(provider => new SettingsCoreUpdateDiscoveryStore(
            provider.GetRequiredService<ISettingsStore>(),
            message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
        services.AddSingleton<ICoreUpdatePolicyStore, SettingsCoreUpdatePolicyStore>();
        services.AddSingleton<IGithubRoutePreferenceStore, SettingsGithubRoutePreferenceStore>();
        services.AddSingleton<IGithubProxySpeedTester>(provider => new GithubProxySpeedTester(
            provider.GetRequiredService<GithubHttpClientResources>().Download));
        services.AddSingleton<IPlatformCommandExecutor, ProcessCommandExecutor>();
        services.AddSingleton<ICorePullRequestMergeService>(provider => new CorePullRequestMergeService(
            provider.GetRequiredService<AppPaths>().CoreCacheDirectory,
            provider.GetRequiredService<IPlatformCommandExecutor>(),
            message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
        services.AddSingleton<IRuntimeFirewall, FirewallManager>();
        services.AddSingleton<AutostartManager>();
        services.AddSingleton<IAutostartService, PlatformAutostartService>();
        services.AddSingleton<NativeToastNotificationService>();
        services.AddSingleton<IDesktopNotificationService>(provider => new PreferenceDesktopNotificationService(
            provider.GetRequiredService<NativeToastNotificationService>(), provider.GetRequiredService<ISettingsStore>()));
        // 显式工厂：软件更新检查需要 GitHub Token（同 api.github.com 配额规则），
        // 直接 AddSingleton<T>() 的反射构造拿不到这个可选参数。
        services.AddSingleton<ApplicationUpdateViewModel>(provider => new ApplicationUpdateViewModel(
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IDesktopNotificationService>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            tokenProvider: provider.GetRequiredService<IGithubTokenProvider>()));
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
        services.AddSingleton<IVerifiedProcessTerminator, WindowsProcessTerminator>();
        services.AddSingleton<IRuntimeHealthClient, RuntimeHealthClient>();
        services.AddSingleton<ICoreLogClient, CoreLogClient>();
        services.AddSingleton<IRuntimeManagementClient, RuntimeManagementClient>();
        services.AddTransient<ServiceManagementPageViewModel>();
        services.AddSingleton<Func<ServiceManagementPageViewModel>>(provider => () => provider.GetRequiredService<ServiceManagementPageViewModel>());
        services.AddSingleton<ICoreRequestRecordsClient, CoreRequestRecordsClient>();
        services.AddSingleton<ILocalRequestRecordStore, LocalRequestRecordStore>();
        services.AddSingleton<ICoreCacheClient, CoreCacheClient>();
        services.AddSingleton<ICoreCacheAnimeClient, CoreCacheAnimeClient>();
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
            provider.GetRequiredService<ICoreCredentialClient>(),
            provider.GetRequiredService<ICoreCacheAnimeClient>(),
            provider.GetRequiredService<PosterImageService>(),
            provider.GetRequiredService<IAppDiagnostics>()));
        services.AddSingleton<INodeSupervisor>(provider => new NodeSupervisor(
            provider.GetRequiredService<IRuntimeHealthClient>(),
            provider.GetRequiredService<IProcessTerminator>(),
            diagnosticSink: message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
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
        services.AddSingleton<CoreManagementService>(provider => new CoreManagementService(
            provider.GetRequiredService<ICoreInstaller>(),
            provider.GetRequiredService<IGithubCoreRemote>(),
            provider.GetRequiredService<IRuntimeController>(),
            () => ReadManagedVariant(
                provider.GetRequiredService<ISettingsStore>(),
                provider.GetRequiredService<AppPaths>()),
            provider.GetRequiredService<RuntimePreparationService>().AcquireReadyLeaseAsync,
            // 安装变更后由结论持有人重新对账：延迟解析，因为处理器本身依赖本服务。
            conclusionReconciler: () => provider.GetRequiredService<CoreUpdateResultHandler>(),
            diagnosticSink: message => provider.GetRequiredService<IAppDiagnostics>().Record(message),
            pullRequestMerge: provider.GetRequiredService<ICorePullRequestMergeService>()));
        services.AddSingleton<ICoreManagementService>(provider => provider.GetRequiredService<CoreManagementService>());
        services.AddSingleton<ICorePullRequestManagementService>(provider => provider.GetRequiredService<CoreManagementService>());
        services.AddSingleton<ICoreUpdateCoordinator>(provider => new CoreUpdateCoordinator(
            provider.GetRequiredService<ICoreInstaller>(),
            provider.GetRequiredService<IGithubCoreRemote>(),
            provider.GetRequiredService<ICoreUpdateTimestampStore>(),
            provider.GetRequiredService<ICoreUpdateDiscoveryStore>(),
            diagnosticSink: message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
        services.AddSingleton<CoreUpdateResultHandler>(provider => new CoreUpdateResultHandler(
            provider.GetRequiredService<ICoreManagementService>(),
            provider.GetRequiredService<ICoreUpdateCoordinator>(),
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
            themeService: provider.GetRequiredService<ThemeService>(),
            // 服务分类里的「随服务启动内网穿透」开关与穿透页监控开关是同一个值。
            frpTunnel: provider.GetRequiredService<IFrpTunnelService>())
        {
            Outbound = provider.GetRequiredService<OutboundDirectViewModel>(),
        });
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
        services.AddSingleton<IActiveCoreVariantStore>(provider => new SettingsActiveCoreVariantStore(
            provider.GetRequiredService<ISettingsStore>()));
        services.AddSingleton<IRuntimeVariantSwitchService>(provider => new RuntimeVariantSwitchService(
            provider.GetRequiredService<IActiveCoreVariantStore>(),
            provider.GetRequiredService<IRuntimeController>(),
            diagnosticSink: message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
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
            provider.GetRequiredService<RuntimePreparationService>(),
            provider.GetRequiredService<ICorePullRequestManagementService>(),
            provider.GetRequiredService<IActiveCoreVariantStore>(),
            provider.GetRequiredService<IRuntimeVariantSwitchService>().SwitchAsync));
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
            provider.GetRequiredService<Func<LocalDanmuPageViewModel>>(),
            provider.GetRequiredService<Func<FrpTunnelPageViewModel>>()));
        // 内网穿透：设置（明文进 settings.properties / 秘密进 DPAPI）、二进制安装、进程监督三段各自独立，
        // 由 FrpTunnelService 编排；概览卡片与穿透页共用它这一份快照。
        services.AddSingleton<FrpSettingsStore>(provider => new FrpSettingsStore(
            provider.GetRequiredService<ISettingsStore>(),
            new WindowsProtectedStringStore(paths.FrpTokenFile, "DanmuApi.Windows.FrpToken.v1"),
            new WindowsProtectedStringStore(paths.FrpAdminPasswordFile, "DanmuApi.Windows.FrpAdminPassword.v1"),
            new WindowsProtectedDocumentStore(paths.FrpConfigTextFile)));
        services.AddSingleton<IFrpNativeVerifier>(_ => new FrpNativeVerifier());
        services.AddSingleton<IFrpBinaryInstaller>(provider => new FrpBinaryInstaller(
            provider.GetRequiredService<AppPaths>(),
            provider.GetRequiredService<IGithubFileDownloader>(),
            provider.GetRequiredService<GithubHttpClientResources>().Download,
            diagnosticSink: message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
        services.AddSingleton<FrpReleaseDiscovery>(provider => new FrpReleaseDiscovery(
            provider.GetRequiredService<GithubHttpClientResources>().Api,
            provider.GetRequiredService<IGithubTokenProvider>()));
        services.AddSingleton<IFrpAdminClient>(provider => new FrpAdminClient(
            provider.GetRequiredService<GithubHttpClientResources>().Download));
        services.AddSingleton<IFrpSupervisor>(provider => new FrpSupervisor(
            provider.GetRequiredService<IFrpAdminClient>(),
            provider.GetRequiredService<IVerifiedProcessTerminator>(),
            diagnosticSink: message => provider.GetRequiredService<IAppDiagnostics>().Record(message)));
        services.AddSingleton<IFrpTunnelService>(provider => new FrpTunnelService(
            provider.GetRequiredService<FrpSettingsStore>(),
            provider.GetRequiredService<IFrpBinaryInstaller>(),
            provider.GetRequiredService<IFrpSupervisor>(),
            provider.GetRequiredService<AppPaths>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            // 穿透跟随弹幕服务生命周期：服务停它也停，服务起（且用户开了开关）它也起。
            provider.GetRequiredService<IRuntimeController>(),
            () => DesktopConfigReader.Read(
                provider.GetRequiredService<ISettingsStore>(),
                provider.GetRequiredService<AppPaths>().NodeProjectDirectory).Port,
            provider.GetRequiredService<IFrpNativeVerifier>()));
        services.AddSingleton<Func<FrpTunnelPageViewModel>>(provider => () =>
        {
            // 监控页底部的「去配置」要切到同一个页签容器里的「配置」页签。
            // 页面是自己创建自己的（下面的工厂在构造函数里就被调用一次），
            // 所以这里先把 page 变量捕获进闭包，等用户真的点按钮时它早已赋值。
            FrpTunnelPageViewModel? page = null;
            page = new FrpTunnelPageViewModel(
            () => new FrpTunnelMonitorViewModel(
                provider.GetRequiredService<IFrpTunnelService>(),
                provider.GetRequiredService<IRuntimeController>(),
                provider.GetRequiredService<IUiDialogService>(),
                provider.GetRequiredService<IAppDiagnostics>(),
                provider.GetRequiredService<AppPaths>(),
                provider.GetRequiredService<FrpReleaseDiscovery>(),
                provider.GetRequiredService<IGithubRoutePreferenceStore>(),
                provider.GetRequiredService<IGithubProxySpeedTester>(),
                () => DesktopConfigReader.Read(
                    provider.GetRequiredService<ISettingsStore>(),
                    provider.GetRequiredService<AppPaths>().NodeProjectDirectory).Port,
                openConfiguration: () => page?.SelectTab(FrpTunnelTab.Configuration)),
            () => new FrpTunnelConfigViewModel(
                provider.GetRequiredService<IFrpTunnelService>(),
                provider.GetRequiredService<IUiDialogService>(),
                provider.GetRequiredService<IAppDiagnostics>(),
                () => DesktopConfigReader.Read(
                    provider.GetRequiredService<ISettingsStore>(),
                    provider.GetRequiredService<AppPaths>().NodeProjectDirectory).Port),
            () => new FrpTunnelLogViewModel(
                provider.GetRequiredService<IFrpTunnelService>(),
                provider.GetRequiredService<IUiDialogService>(),
                provider.GetRequiredService<IAppDiagnostics>(),
                provider.GetRequiredService<AppPaths>()));
            return page;
        });
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
            provider.GetRequiredService<ICoreManagementService>(),
            // 侧栏「有更新」卡片的权威来源：前台/后台/托盘/手动的核心检查结果都汇到协调器。
            coreUpdateCoordinator: provider.GetRequiredService<ICoreUpdateCoordinator>(),
            frp: provider.GetRequiredService<IFrpTunnelService>()));
        services.AddSingleton<AppLifecycleCoordinator>(provider => new AppLifecycleCoordinator(
            provider.GetRequiredService<IRuntimeController>(),
            provider.GetRequiredService<ISettingsStore>(),
            provider.GetRequiredService<IUiDialogService>(),
            provider.GetRequiredService<IAppDiagnostics>(),
            provider.GetRequiredService<ICoreUpdateScheduler>(),
            desktop,
            ownerProvider,
            provider.GetRequiredService<IFrpTunnelService>()));
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
                variant =>
                {
                    if (viewModel.CorePage is { } corePage)
                    {
                        corePage.SelectedVariant = variant;
                    }
                    viewModel.NavigateTo("core");
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

    internal static AppPaths CreateConfiguredPaths()
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

    /// <summary>
    /// 启动时把上个进程落盘的「核心有更新」结论接回协调器，主窗口构造时读 <c>LastResult</c>
    /// 就能直接显示侧栏卡片，无需等一次联网检查。读不动只记诊断并跳过：
    /// 丢的是一条可选缓存，下一次自动检查会照常重建它。
    /// </summary>
    private static void RestorePersistedCoreDiscovery(ServiceProvider services, AppPaths paths)
    {
        try
        {
            var variant = ReadManagedVariant(services.GetRequiredService<ISettingsStore>(), paths);
            // 先解析结论持有人：它订阅协调器的结论变化，必须在接回之前就位，
            // 否则恢复出来的这条结论只更新了侧栏，托盘菜单会少一份待更新项。
            _ = services.GetRequiredService<CoreUpdateResultHandler>();
            services.GetRequiredService<ICoreUpdateCoordinator>().ReconcileDiscovery(variant);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            services.GetRequiredService<IAppDiagnostics>().Record("恢复核心更新提示失败（不影响启动）", error);
        }
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
