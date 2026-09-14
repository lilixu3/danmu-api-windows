using System.Runtime.InteropServices;

namespace DanmuApi.Core;

/// <summary>
/// The Windows architecture this process runs as, named the way release assets and the signed update
/// manifest name it. A 32-bit build running on 64-bit Windows reports win-x86, which is exactly what
/// its own package is called, so updates keep matching the installed package.
/// </summary>
public static class ApplicationArchitecture
{
    public static string Current { get; } = Name(RuntimeInformation.ProcessArchitecture);

    public static string Name(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.X86 => "win-x86",
        Architecture.Arm64 => "win-arm64",
        var other => throw new InvalidOperationException("不支持的进程架构：" + other),
    };
}
