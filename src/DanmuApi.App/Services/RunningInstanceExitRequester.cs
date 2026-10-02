using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DanmuApi.Platform;

namespace DanmuApi.App.Services;

/// <summary>
/// 「请正在运行的实例退出」的入口，供**安装器**调用。
///
/// 安装器（Inno Pascal）没有 TCP 能力，无法直接说那条带令牌的 loopback 协议，
/// 所以由本进程代它说：把 <see cref="InstanceCommand.REQUEST_EXIT"/> 发给正在运行的实例，
/// 然后等它真正释放单实例锁。退出码约定：
/// <list type="bullet">
/// <item>0 = 运行中的实例已退出（锁已释放）</item>
/// <item>1 = 没有正在运行的实例，或请求发不出去</item>
/// <item>2 = 请求已发出，但等待超时（实例仍在运行）</item>
/// </list>
/// 诊断只写标准错误，**绝不输出令牌**。
/// </summary>
internal static class RunningInstanceExitRequester
{
    public const string Argument = "--request-exit";
    public const string InstallerProbeArgument = "--installer-instance-probe";
    public const string InstallerExitArgument = "--installer-request-exit";
    internal const int InstanceHeld = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    // Installer relays must run under the original user's non-elevated token. When Setup
    // was itself started elevated, ExecAsOriginalUser cannot de-elevate it: fail closed,
    // rather than calling an administrator-profile empty lock proof of the user's exit.
    public static int RunInstaller(bool probeOnly, TimeSpan timeout) =>
        RunInstaller(probeOnly, timeout, IsTokenElevated, () => new AppPaths());

    internal static int RunInstaller(bool probeOnly, TimeSpan timeout, Func<bool> isElevated, Func<AppPaths> pathsFactory)
    {
        try
        {
            if (isElevated())
            {
                Console.Error.WriteLine("安装器退出中继拒绝管理员令牌；请以普通用户启动安装器，再授权 UAC。无法验证原用户实例时不会继续安装。");
                return 1;
            }
            // Instance files are always in fixed roaming AppData, not runtime_root or settings
            // content. The original-user relay never loads settings or any executable from endpoint.
            var paths = pathsFactory();
            if (!probeOnly) return Run(timeout, paths);
            using var instanceLock = new AppInstanceLock(paths, _ => { });
            var result = instanceLock.TryAcquireDetailed();
            if (result.Succeeded)
            {
                var release = instanceLock.Release();
                if (release.Succeeded) return 0;
                Console.Error.WriteLine($"安装器实例探测释放锁失败: {release.Diagnostic}");
                return 1;
            }
            if (result.AlreadyOwned) return InstanceHeld;
            Console.Error.WriteLine($"安装器实例探测失败: {result.Diagnostic}");
            return 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"安装器原用户实例验证失败: {error.Message}");
            return 1;
        }
    }

    private static bool IsTokenElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, 20 /* TokenElevation */, out var elevation, sizeof(int), out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "读取安装器中继令牌权限失败");
        return elevation != 0;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token,
        int informationClass, out int information, int informationLength, out int returnLength);

    public static int Run(TimeSpan timeout)
    {
        AppPaths paths;
        try
        {
            paths = new AppPaths();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"读取运行目录设置失败: {error.Message}");
            return 1;
        }

        return Run(timeout, paths);
    }

    /// <summary>可测重载：安装器路径不可注入，但这条逻辑必须能在临时目录里被真实驱动。</summary>
    internal static int Run(TimeSpan timeout, AppPaths paths)
    {
        // 锁在轮询期间会把"已被占用"记成诊断；这里故意不逐条转发——60 秒 × 200ms 会刷出几百行，
        // 把安装器日志淹掉。只在最后给一条结论。
        using var instanceLock = new AppInstanceLock(paths, _ => { });
        var command = instanceLock.SendCommand(InstanceCommand.REQUEST_EXIT);

        // 完成判据是**锁被真正释放**，而不是那句 OK：收到 OK 只代表请求被受理，
        // 实例这时还在停服务、存档、释放资源；反过来，实例也可能在回 OK 之前就退出完毕
        // （端点已被它自己删掉），所以发命令失败同样要继续看锁。
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var acquire = instanceLock.TryAcquireDetailed();
            if (acquire.Succeeded)
            {
                // 抢到即证明前一个实例已经放手；立刻还回去，不占着安装器的路。
                var release = instanceLock.Release();
                if (!release.Succeeded)
                {
                    Console.Error.WriteLine($"退出请求完成后释放探测锁失败: {release.Diagnostic}");
                    return 2;
                }
                if (!command.Succeeded)
                {
                    // 没应答但锁确实空了：实例在回话前就退完了，同样算已退出。
                    Console.Error.WriteLine("运行实例已退出（未及应答退出请求）。");
                }

                return 0;
            }

            if (!acquire.AlreadyOwned)
            {
                Console.Error.WriteLine($"等待运行实例退出时读取单实例锁失败: {acquire.Diagnostic}");
                return 2;
            }

            Thread.Sleep(PollInterval);
        }

        // 走到这里说明锁一直没松：实例仍在运行（旧版本不认这条命令，或退出卡住）。
        // 绝不能报成功——安装器据此决定是否可以覆盖正在运行的程序文件。
        Console.Error.WriteLine(
            $"运行中的实例未在 {timeout.TotalSeconds:0} 秒内退出"
            + (command.Succeeded ? "（已收到退出请求但未完成）" : $"（未响应退出请求：{command.Diagnostic}）")
            + "；请从托盘退出后重试。");
        return 2;
    }
}
