using System.Globalization;
using DanmuApi.Core.Frp;

namespace DanmuApi.App.Services;

/// <summary>概览页穿透卡片渲染所需的输入（全部来自快照与设置，不含任何推导）。</summary>
public sealed record FrpSurfaceInput(
    FrpTunnelState State,
    string? RemoteAddress,
    string? Diagnostic,
    FrpRole Role,
    FrpProxyKind ProxyKind,
    int ServerBindPort,
    bool Installed,
    bool ServiceRunning,
    string CoreToken);

/// <summary>
/// 概览页穿透卡片与连接地址行的文案/地址推导。
///
/// 抽成纯函数是为了能直接测：这几句话组合起来决定用户"到底通没通、地址是什么"，
/// 而它们又依赖"服务在不在跑、装没装 frp"等互相独立的状态，放在外壳视图模型里就只能靠人肉推演。
/// 原则：地址只在 frp 报告代理运行且真的给了 remote_addr 时给出，且**带上核心的访问 TOKEN**
/// （与局域网地址同形：http://host:port/TOKEN）；服务端角色不编造公网地址。
/// </summary>
public static class FrpStatusPresentation
{
    public static string StatusText(FrpSurfaceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.State switch
        {
            FrpTunnelState.Stopped => "未启动",
            FrpTunnelState.Starting => "正在连接服务器",
            FrpTunnelState.Running => "穿透正常",
            FrpTunnelState.Reconnecting => "连接中断，重连中",
            FrpTunnelState.Stopping => "正在停止",
            FrpTunnelState.Failed => "穿透失败",
            _ => input.State.ToString(),
        };
    }

    public static string AddressText(FrpSurfaceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.State != FrpTunnelState.Running)
        {
            return string.Empty;
        }

        if (input.Role == FrpRole.Server)
        {
            return $"本机穿透端口 {input.ServerBindPort.ToString(CultureInfo.InvariantCulture)}（需公网 IP 或端口映射）";
        }

        return FrpPublicAddress.Build(new FrpPublicAddressInput(
            input.State,
            input.Role,
            input.ProxyKind,
            input.RemoteAddress,
            input.CoreToken));
    }

    public static bool HasAddress(FrpSurfaceInput input) => AddressText(input).Length > 0;

    /// <summary>该地址是否是可复制的 API 链接（服务端角色只有一句端口说明）。</summary>
    public static bool IsCopyableAddress(FrpSurfaceInput input) =>
        input.Role == FrpRole.Client && AddressText(input).StartsWith("http", StringComparison.Ordinal);

    public static string HintText(FrpSurfaceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Diagnostic is { Length: > 0 } diagnostic)
        {
            return diagnostic;
        }

        if (!input.Installed)
        {
            return "尚未安装 frp；在工具页的「内网穿透」里下载安装后即可使用。";
        }

        return input.State switch
        {
            FrpTunnelState.Running when input.Role == FrpRole.Client => input.ServiceRunning
                ? "外网可通过上面的地址访问本机弹幕 API（地址含 Token）。"
                : "穿透已就绪，但弹幕服务当前未运行，外部访问会失败。",
            FrpTunnelState.Running => "穿透服务已在本机监听。",
            FrpTunnelState.Stopped => "未启动；在工具页的「内网穿透」里启动。",
            _ => "状态正在刷新。",
        };
    }

    /// <summary>穿透在跑但弹幕服务没跑：最容易"看着正常其实访问不了"的组合，概览上必须点出来。</summary>
    public static bool ShowServiceWarning(FrpSurfaceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Role == FrpRole.Client
               && input.State == FrpTunnelState.Running
               && !input.ServiceRunning;
    }
}
