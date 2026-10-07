namespace DanmuApi.App.Services;

/// <summary>
/// 只放行第一次调用的闸门。启动准备（含更新后回执与弹幕服务恢复）必须只跑一次：主窗口每被打开
/// 一次都会走到准备入口，托盘隐藏后再显示同样算，重复执行会把回执、恢复报告与服务操作再写一遍。
/// </summary>
internal sealed class SingleShotGate
{
    public bool Entered { get; private set; }

    public bool TryEnter()
    {
        if (Entered) return false;
        Entered = true;
        return true;
    }
}
