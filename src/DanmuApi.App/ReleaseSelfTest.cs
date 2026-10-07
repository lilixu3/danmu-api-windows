using System.Diagnostics;
using System.Text.Json;
using DanmuApi.Core.ApplicationUpdates;
using DanmuApi.App.Services;
using DanmuApi.Platform;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace DanmuApi.App;

internal static class ReleaseSelfTest
{
    public static int Run(string reportPath)
    {
        var testId = "DanmuApi.ReleaseProbe." + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), testId);
        var messages = new List<string>();
        var diagnostics = new ProbeDiagnostics(messages);
        var code = 1;
        try
        {
            var notificationOnly = Environment.GetEnvironmentVariable("DANMU_NOTIFICATION_ICON_PROBE") == "1";
            messages.Add($"隔离身份：{testId}；通知专用探针：{notificationOnly}");
            if (!notificationOnly)
            {
            VerifyUpdateStartupReceipt(messages);
            var paths = new AppPaths(directory, Path.Combine(directory, "settings"));
            var preparationWatch = Stopwatch.StartNew();
            BundledRuntimePreparer.Prepare(Path.Combine(AppContext.BaseDirectory, "runtime-bundle"), paths);
            messages.Add($"首次准备耗时：{preparationWatch.Elapsed.TotalMilliseconds:0.0} ms");
            if (Directory.EnumerateDirectories(paths.NodeProjectDirectory, "danmu_api*").Any()) throw new IOException("依赖包意外包含核心");
            var start = new ProcessStartInfo(Path.Combine(paths.RuntimeDirectory, "node.exe"))
            {
                WorkingDirectory = paths.NodeProjectDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("require('./runtime-polyfills.js');require('./startup-failure.js');require('./favorite-scheduler-host.js');for(const x of Object.keys(require('./package.json').dependencies)){require.resolve(x)};console.log(process.version)");
            using var node = Process.Start(start) ?? throw new IOException("Node未启动");
            var stdout = node.StandardOutput.ReadToEndAsync();
            var stderr = node.StandardError.ReadToEndAsync();
            if (!node.WaitForExit(15000)) { node.Kill(true); throw new TimeoutException("Node依赖探针超时"); }
            if (node.ExitCode != 0) throw new IOException("Node依赖检查失败：" + stderr.GetAwaiter().GetResult());
            var nodeVersion = stdout.GetAwaiter().GetResult().Trim();
            messages.Add("Node及生产依赖解析通过：" + nodeVersion);
            // The runtime the package carries must be the runtime this build was compiled against,
            // in the architecture it was packaged for. Both are proven against the deployed binary,
            // not against the manifest that describes it.
            var bundle = ReadRuntimeBuildIdentity(Path.Combine(AppContext.BaseDirectory, "runtime-bundle"));
            var hostArchitecture = BundledRuntimePreparer.HostArchitecture();
            if (!string.Equals(bundle.Arch, hostArchitecture, StringComparison.Ordinal))
                throw new IOException($"运行环境记录架构 {bundle.Arch} 与当前进程架构 {hostArchitecture} 不一致");
            var deployed = Path.Combine(paths.RuntimeDirectory, "node.exe");
            var machine = ExecutableImage.ArchitectureName(ExecutableImage.Machine(deployed));
            if (!string.Equals(machine, bundle.Arch, StringComparison.Ordinal))
                throw new IOException($"部署的 node.exe 实际架构 {machine} 与运行环境记录 {bundle.Arch} 不一致");
            if (!string.Equals(nodeVersion, "v" + bundle.NodeVersion, StringComparison.Ordinal))
                throw new IOException($"node.exe 实际版本 {nodeVersion} 与运行环境记录 {bundle.NodeVersion} 不一致");
            messages.Add($"运行环境记录 Node {bundle.NodeVersion}/{bundle.Arch}，实际 {nodeVersion}/{machine} 一致");
            VerifyOutboundRuntime(paths, messages);
            var config = Path.Combine(paths.NodeProjectDirectory, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, ".env"), "USER_SENTINEL=preserve");
            preparationWatch.Restart();
            BundledRuntimePreparer.Prepare(Path.Combine(AppContext.BaseDirectory, "runtime-bundle"), paths);
            messages.Add($"重复准备耗时：{preparationWatch.Elapsed.TotalMilliseconds:0.0} ms");
            if (File.ReadAllText(Path.Combine(config, ".env")) != "USER_SENTINEL=preserve") throw new IOException("配置被覆盖");
            messages.Add("空目录准备通过；无核心；重复准备保留用户配置");
            }
            var iconDirectory = Path.Combine(directory, "通知 图标 & ' assets");
            var iconPath = NativeToastNotificationService.EnsureIcon(iconDirectory);
            using var resource = typeof(NativeToastNotificationService).Assembly.GetManifestResourceStream("DanmuApi.NotificationIcon.png")
                ?? throw new IOException("单文件程序集缺少通知 PNG 资源");
            using var buffer = new MemoryStream();
            resource.CopyTo(buffer);
            var png = File.ReadAllBytes(iconPath);
            if (!png.AsSpan().SequenceEqual(buffer.ToArray())) throw new IOException("提取 PNG 与内嵌资源不一致");
            if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new IOException("通知资源不是 PNG");
            if (!png.AsSpan(12, 4).SequenceEqual("IHDR"u8)
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != 256
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) != 256)
                throw new IOException("通知 PNG IHDR 尺寸错误");
            messages.Add($"内嵌 PNG 提取/字节一致/IHDR 256x256通过：{iconPath}");
            const string title = "弹幕 API · 隔离图标验证";
            const string message = "单文件 PNG/XML/注册/通知提交验证，不会启动核心。";
            var xml = System.Xml.Linq.XDocument.Parse(NativeToastNotificationService.BuildToastXml(title, message, "danmuapi://open", null, iconPath));
            var image = xml.Descendants("image").Single();
            var source = new Uri((string?)image.Attribute("src") ?? throw new IOException("Toast 缺少图片 URI"));
            if ((string?)image.Attribute("placement") != "appLogoOverride" || !source.IsFile || source.LocalPath != iconPath)
                throw new IOException("Toast 图片未指向隔离 PNG");
            if (!xml.Descendants("text").Select(item => item.Value).SequenceEqual(new[] { title, message }))
                throw new IOException("Toast 文本不一致");
            messages.Add("Toast XML appLogoOverride/file URI/文本通过：" + xml.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
            var service = new NativeToastNotificationService(diagnostics)
            {
                RegisteredApplicationId = testId, RegisterProtocol = false, TestIconDirectory = iconDirectory,
            };
            var notification = service.ShowAsync(title, message).GetAwaiter().GetResult();
            if (!notification.Succeeded) throw new IOException(notification.Diagnostic);
            using var identity = Registry.CurrentUser.OpenSubKey($@"Software\Classes\AppUserModelId\{testId}");
            if (!Equals(identity?.GetValue("IconUri"), iconPath)) throw new IOException("隔离 AUMID IconUri 不是预期 PNG");
            messages.Add("隔离 AUMID PNG 注册值及最终发布EXE通知提交通过");
            code = 0;
        }
        catch (Exception error) { messages.Add(error.ToString()); }
        finally
        {
            try
            {
                ToastNotificationManager.History.Clear(testId);
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{testId}", false);
                File.Delete(NotificationShortcut.PathFor(testId));
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception error) { messages.Add("测试资源清理失败：" + error.Message); code = 1; }
            File.WriteAllLines(reportPath, messages);
        }
        return code;
    }

    private static void VerifyUpdateStartupReceipt(List<string> messages)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("发行探针缺少真实程序路径");
        var productVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion
            ?? throw new IOException("发行探针缺少程序版本");
        _ = SemanticVersion.Parse(productVersion);
        var assemblyVersion = typeof(ReleaseSelfTest).Assembly.GetName().Version
            ?? throw new IOException("发行探针缺少程序集版本");
        var expectedVersion = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        if (!string.Equals(productVersion, expectedVersion, StringComparison.Ordinal))
            throw new IOException("发行程序 ProductVersion 必须与签名清单使用的纯版本号精确一致，供旧便携助手校验");
        var directory = Path.Combine(ApplicationUpdateHelper.JobRoot, Guid.NewGuid().ToString("N"));
        for (var ancestor = Path.GetDirectoryName(directory); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.Exists(ancestor) || Directory.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("发行回执探针目录不能包含链接");
        if (File.Exists(directory) || Directory.Exists(directory)) throw new IOException("发行回执探针目录已存在");
        Directory.CreateDirectory(directory);
        try
        {
            using var process = Process.GetCurrentProcess();
            var args = new[] { "--app-update-receipt", directory };
            var jobPath = Path.Combine(directory, "job.json");
            var receiptPath = Path.Combine(directory, "startup.ok");
            foreach (var receiptVersion in new[] { expectedVersion, expectedVersion + "+receipt.regression" })
            foreach (var resume in new[] { false, true })
            {
                var job = new ApplicationUpdateJob(process.Id, process.StartTime.ToUniversalTime().Ticks,
                    AppContext.BaseDirectory, "release-self-test.exe", "installer", receiptVersion, resume);
                var jobBytes = JsonSerializer.SerializeToUtf8Bytes(job);
                File.WriteAllBytes(jobPath, jobBytes);
                ApplicationUpdateHelper.ValidateJobDirectory(directory);
                if (ApplicationUpdateHelper.ShouldResumeService(args) != resume)
                    throw new IOException("发行回执探针服务恢复标志不一致");
                ApplicationUpdateHelper.AcknowledgeStartup(args);
                if (!File.ReadAllBytes(receiptPath).AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(receiptVersion))
                    || File.Exists(receiptPath + ".tmp") || !File.ReadAllBytes(jobPath).AsSpan().SequenceEqual(jobBytes))
                    throw new IOException("发行回执探针内容、原子发布或任务保留验证失败");
                File.Delete(receiptPath);
            }
        }
        catch (Exception error)
        {
            messages.Add("更新启动回执探针失败：" + error);
            throw;
        }
        finally
        {
            try
            {
                ApplicationUpdateHelper.ValidateJobDirectory(directory);
                Directory.Delete(directory, true);
                if (Directory.Exists(directory)) throw new IOException("发行回执探针目录未清理");
            }
            catch (Exception error)
            {
                messages.Add("更新启动回执探针清理失败：" + error);
                throw;
            }
        }
        messages.Add($"UPDATE_STARTUP_RECEIPT=PASS; expected={expectedVersion}; product={productVersion}; expectedMetadata=false,true; resume=false,true; exactUtf8=true; jobUnchanged=true; temporaryAbsent=true; ownedJobRemoved=true");
        messages.Add($"LEGACY_PORTABLE_PRODUCT_VERSION=PASS; expected={expectedVersion}; product={productVersion}; exact=true");
    }

    private static void VerifyOutboundRuntime(AppPaths paths, List<string> messages)
    {
        var settings = new OutboundSettingsStore(paths);
        settings.Write(OutboundSettings.Default with { Enabled = true });
        var start = new ProcessStartInfo(Path.Combine(paths.RuntimeDirectory, "node.exe"))
        {
            WorkingDirectory = paths.NodeProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["DANMU_API_RUNTIME_IDENTITY"] = "release-outbound-" + Guid.NewGuid().ToString("N");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("""
            const fs=require('node:fs');
            const path=require('node:path');
            const {createAppOutboundRuntime}=require('./app-outbound-runtime.js');
            const dir=path.join(process.cwd(),'config','outbound');
            const runtime=createAppOutboundRuntime({
              configPath:path.join(dir,'settings.json'),
              helperPath:path.join(process.cwd(),'outbound','danmu-outbound.exe'),
              log:(...args)=>console.error(...args)
            });
            (async()=>{
              let result;
              try {
                await runtime.start();
                const state=runtime.safeSnapshot();
                if(state.status!=='ready'||!Number.isInteger(state.helperPid)||state.helperPid<=0||state.protocolVersion!==1||!state.helperVersion)
                  throw new Error('Outbound helper was not ready: '+state.reason);
                const session=JSON.parse(fs.readFileSync(path.join(dir,'session.json'),'utf8'));
                if(session.helperPid!==state.helperPid||session.nodePid!==process.pid||session.runtimeIdentity!==process.env.DANMU_API_RUNTIME_IDENTITY)
                  throw new Error('Outbound session ownership mismatch');
                result={helperPid:state.helperPid,version:state.helperVersion,protocolVersion:state.protocolVersion};
              } finally { await runtime.stop(); }
              if(fs.existsSync(path.join(dir,'session.json')))throw new Error('Outbound session was not removed');
              const status=JSON.parse(fs.readFileSync(path.join(dir,'status.json'),'utf8'));
              if(status.status!=='off'||status.helperPid!==null)throw new Error('Outbound helper did not stop');
              try { process.kill(result.helperPid,0); throw new Error('Outbound helper process remained alive'); }
              catch(error){if(error.code!=='ESRCH')throw error;}
              console.log(JSON.stringify(result));
            })().catch(error=>{console.error(error.stack||error.message);process.exitCode=1;});
            """);
        using var node = Process.Start(start) ?? throw new IOException("增强直连隔离探针未启动");
        var stdout = node.StandardOutput.ReadToEndAsync();
        var stderr = node.StandardError.ReadToEndAsync();
        if (!node.WaitForExit(30000))
        {
            node.Kill(entireProcessTree: true);
            node.WaitForExit(5000);
            throw new TimeoutException("增强直连隔离探针超时，已终止探针进程树");
        }
        var output = stdout.GetAwaiter().GetResult().Trim();
        var diagnostic = stderr.GetAwaiter().GetResult().Trim();
        if (node.ExitCode != 0) throw new IOException($"增强直连隔离探针失败（exit {node.ExitCode}）：{diagnostic}");
        using var report = JsonDocument.Parse(output);
        var version = report.RootElement.GetProperty("version").GetString();
        if (report.RootElement.GetProperty("protocolVersion").GetInt32() != 1 || string.IsNullOrWhiteSpace(version))
            throw new IOException("增强直连隔离探针返回未知协议或版本");
        var helperPath = Path.Combine(paths.NodeProjectDirectory, "outbound", "danmu-outbound.exe");
        var machine = ExecutableImage.ArchitectureName(ExecutableImage.Machine(helperPath));
        if (machine != BundledRuntimePreparer.HostArchitecture()) throw new IOException("增强直连组件实际架构与宿主不一致");
        messages.Add($"增强直连随包组件实际启动/鉴权握手/协议1/退出清理通过：{version}/{machine}；未发公网请求");
        var before = File.ReadAllText(settings.SettingsPath);
        BundledRuntimePreparer.Prepare(Path.Combine(AppContext.BaseDirectory, "runtime-bundle"), paths);
        if (File.ReadAllText(settings.SettingsPath) != before) throw new IOException("重复准备覆盖了增强直连用户配置");
        messages.Add("重复准备保留增强直连配置通过");
    }

    private sealed record RuntimeBuildIdentity(string Version, string NodeVersion, string Arch);

    private static RuntimeBuildIdentity ReadRuntimeBuildIdentity(string bundleDirectory)
    {
        var path = Path.Combine(bundleDirectory, "runtime-build.json");
        if (!File.Exists(path)) throw new IOException("随包运行环境缺少 runtime-build.json：" + path);
        using var stream = File.OpenRead(path);
        var identity = JsonSerializer.Deserialize<RuntimeBuildIdentity>(stream);
        if (identity is null || string.IsNullOrWhiteSpace(identity.NodeVersion) || string.IsNullOrWhiteSpace(identity.Arch))
            throw new IOException("运行环境记录缺少 Node 版本或架构");
        return identity;
    }

    private sealed class ProbeDiagnostics(List<string> messages) : IAppDiagnostics
    {
        public string? LastDiagnostic { get; private set; }
        public void Record(string message, Exception? error = null) { LastDiagnostic = message; messages.Add(message); }
    }
}
