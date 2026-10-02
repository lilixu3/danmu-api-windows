namespace DanmuApi.App.ViewModels;

/// <summary>
/// 监控页「代理状态」的一行。
///
/// 为什么不是一整行文本：代理状态里混了两类信息 —— 状态词（该用胶囊表达语义色）
/// 和只有这条代理才有的细节（远端地址或 frp 给的失败原因）。
/// 拼成一串字符串就没法分别排版，也没法把失败标红（旧版的 <c>ProxiesText</c> 就是这样）。
/// </summary>
public sealed record FrpProxyLine(
    string Name,
    string Type,
    string StateText,
    string Detail,
    bool IsRunning,
    bool IsFailed)
{
    /// <summary>名称与类型合成一个等宽标签：<c>danmu-api · TCP</c>。</summary>
    public string Caption => $"{Name} · {Type}";

    public bool HasDetail => Detail.Length > 0;
}
