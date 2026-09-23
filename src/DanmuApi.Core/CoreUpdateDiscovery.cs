namespace DanmuApi.Core;

/// <summary>
/// 一次**成功**的核心更新检查得出的结论：本地安装的提交不是远端分支最新提交。
/// 这条结论是可复用的知识，必须落盘 —— 否则进程一退，「有更新」就随内存一起消失，
/// 用户重启后就再也看不到侧栏那张卡片。
///
/// 能推翻它的只有两件事：下一次成功检查（本地已是远端），或本机安装被替换
/// （装上了那个提交 / 回退 / 重装）。**时间流逝本身不是证据**，所以这里没有过期时间。
/// </summary>
public sealed record CoreUpdateDiscovery(
    ManagedCoreVariant Variant,
    string LocalSha,
    string RemoteSha,
    string RemoteTitle,
    DateTimeOffset CheckedAt);

public interface ICoreUpdateDiscoveryStore
{
    /// <summary>读取上次成功检查发现的可用更新；没有或已不可用时返回 null。</summary>
    CoreUpdateDiscovery? Read(ManagedCoreVariant variant);

    void Write(CoreUpdateDiscovery discovery);

    /// <summary>清除某变体的发现记录（成功检查确认已是最新、或结论失效时调用）。</summary>
    void Clear(ManagedCoreVariant variant);
}
