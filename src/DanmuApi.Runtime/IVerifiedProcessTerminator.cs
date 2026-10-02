using System.Diagnostics;

namespace DanmuApi.Runtime;

/// <summary>
/// 按"预期可执行文件 + 预期命令行片段"双重验证之后再终止进程的标准入口。
///
/// 与 <see cref="IProcessTerminator"/>（node.exe + main.js 专用）是同一套安全规则的另一组参数：
/// PID 会在进程退出后被系统复用，只凭 PID 杀进程有杀错别人的风险，因此终止前必须证明
/// 这个 PID 现在跑的就是我们自己启动的那个程序（可执行文件路径 + 命令行里的配置路径）。
/// </summary>
public interface IVerifiedProcessTerminator
{
    /// <param name="expectedExecutablePath">预期可执行文件完整路径。</param>
    /// <param name="expectedArgumentFragment">预期出现在命令行里的参数片段（例如配置文件路径）。</param>
    /// <param name="expectedExecutableLabel">诊断信息里用来称呼这个程序的短名（例如 frpc.exe）。</param>
    /// <param name="expectedArgumentLabel">诊断信息里给参数片段的可读称呼；省略时用参数片段的文件名。</param>
    Task<ProcessTerminationResult> TerminateVerifiedAsync(
        Process process,
        string expectedExecutablePath,
        string expectedArgumentFragment,
        string expectedExecutableLabel,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? expectedArgumentLabel = null);
}
